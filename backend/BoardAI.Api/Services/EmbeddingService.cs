using BoardAI.Api.Models;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Tokenizers.HuggingFace.Tokenizer;

namespace BoardAI.Api.Services;

/// <summary>
/// 使用 ONNX Runtime 加载 BGE，按部署配置取 CLS 或 mean，再 L2 归一化。
/// </summary>
public class EmbeddingService : IDisposable
{
    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;
    private readonly int _dimension;
    private readonly BoundedEmbeddingScheduler _queryScheduler;
    public string Pooling { get; }
    public string ModelDirectoryName { get; }
    private const int MaxLength = 512;

    /// <summary>部署侧模型目录名，用于索引版本元数据（同一目录名意味着同一发布模型）。</summary>
    public string ModelId { get; }

    public EmbeddingService(
        string modelDir,
        string pooling = "mean",
        EmbeddingOptions? options = null,
        ILogger<EmbeddingService>? logger = null)
    {
        options ??= new EmbeddingOptions();
        options.Validate();

        Pooling = EmbeddingProfile.NormalizePooling(pooling);
        var onnxPath = Path.Combine(modelDir, "model.onnx");
        var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");

        if (!File.Exists(onnxPath))
            throw new FileNotFoundException($"ONNX model not found: {onnxPath}");
        if (!File.Exists(tokenizerPath))
            throw new FileNotFoundException($"Tokenizer not found: {tokenizerPath}");

        var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = options.IntraOpThreads,
            InterOpNumThreads = options.InterOpThreads,
        };
        _session = new InferenceSession(onnxPath, sessionOptions);
        _queryScheduler = new BoundedEmbeddingScheduler(
            options.MaxConcurrentInference,
            options.MaxPendingInference,
            logger);
        ModelDirectoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDir)));
        ModelId = EmbeddingProfile.ModelId(ModelDirectoryName, Pooling);

        // 从模型输出中获取实际维度
        _dimension = _session.OutputMetadata["last_hidden_state"].Dimensions[2];

        _tokenizer = Tokenizer.FromFile(tokenizerPath);
    }

    /// <summary>
    /// 查询向量推理的统一异步入口：先通过有界调度器等待并发槽位，再把同步
    /// ONNX CPU 推理放到已获准的线程池工作中执行。队列满会抛出
    /// <see cref="EmbeddingQueueFullException"/>，不会伪造向量或静默丢任务。
    /// 已开始的同步推理无法取消，取消只会阻止尚未开始的排队任务。
    /// </summary>
    public async Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[_dimension];

        return await _queryScheduler.RunAsync(
            () => EmbedCore(text),
            text.Length,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 同步嵌入入口，仅供索引重建等不经过查询调度器的离线/管理路径使用。
    /// 在线查询必须使用 <see cref="EmbedAsync"/>。
    /// </summary>
    public float[] Embed(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new float[_dimension];

        return EmbedCore(text);
    }

    private float[] EmbedCore(string text)
    {
        // Tokenize (with special tokens: [CLS] ... [SEP])
        var encodings = _tokenizer.Encode(text, true);
        var encoding = encodings.First();

        var ids = encoding.Ids;
        if (ids == null || ids.Count == 0)
            return new float[_dimension];

        var seqLen = Math.Min(ids.Count, MaxLength);

        // Create tensors [1, seq_len]
        var inputIdsTensor = new DenseTensor<long>(new[] { 1, seqLen });
        var maskTensor = new DenseTensor<long>(new[] { 1, seqLen });

        for (int i = 0; i < seqLen; i++)
        {
            inputIdsTensor[0, i] = ids[i];
            // AttentionMask 和 Ids 同长度；若为 null 则全部视为有效 token
            var attn = encoding.AttentionMask;
            maskTensor[0, i] = attn != null && i < attn.Count ? attn[i] : 1;
        }
        // BGE's new CLS profile follows HF truncation: keep the final special token.
        // Legacy mean retains its existing coordinates for rollback compatibility.
        if (Pooling == "cls" && ids.Count > MaxLength)
            inputIdsTensor[0, seqLen - 1] = ids[ids.Count - 1];

        // Run inference
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor),
        };

        // BERT 系模型（如 bge-base）需要 token_type_ids；全零 = 全部 token 属于 A 段（单句）。
        // 按模型签名条件添加——不需要该输入的模型（如 bge-small）不喂，避免推理报错。
        if (_session.InputMetadata.ContainsKey("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new[] { 1, seqLen })));
        }

        using var results = _session.Run(inputs);

        var hiddenState = results.First(r => r.Name == "last_hidden_state").AsTensor<float>();
        return PoolAndNormalize(hiddenState, maskTensor, Pooling);
    }

    internal static float[] PoolAndNormalize(Tensor<float> hiddenState, Tensor<long> mask, string pooling)
    {
        var mode = EmbeddingProfile.NormalizePooling(pooling);
        var seqLen = hiddenState.Dimensions[1];
        var dimension = hiddenState.Dimensions[2];
        var embedding = new float[dimension];
        int realTokens = 0;
        for (int t = 0; t < (mode == "cls" ? Math.Min(1, seqLen) : seqLen); t++)
        {
            if (mask[0, t] == 0) continue;
            for (int d = 0; d < dimension; d++)
            {
                embedding[d] += hiddenState[0, t, d];
            }
            realTokens++;
        }

        if (realTokens == 0)
            return embedding;

        for (int d = 0; d < dimension; d++)
            embedding[d] /= realTokens;

        // L2 normalize
        var norm = Math.Sqrt(embedding.Sum(x => (double)x * x));
        if (norm > 1e-8)
        {
            for (int i = 0; i < dimension; i++)
                embedding[i] = (float)(embedding[i] / norm);
        }

        return embedding;
    }

    public int Dimension => _dimension;

    public void Dispose()
    {
        _queryScheduler.Dispose();
        _session.Dispose();
    }
}
