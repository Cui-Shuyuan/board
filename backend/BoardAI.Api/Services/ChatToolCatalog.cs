using System.Text.Json;
using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

/// <summary>
/// legacy 路径的工具 Schema 真值。内容、顺序和序列化指纹保持原样；
/// 协议执行与结果投影由独立协作类负责。
/// </summary>
internal static class ChatToolCatalog
{
    internal static List<ToolDefinition> BuildTools(string gameId)
    {
        // 架构：LLM 只提交查询计划，规则检索全部由程序执行。
        // 这些检索接口只作为程序内部能力被 ExecutePlan 使用，不暴露给 LLM。
        return new List<ToolDefinition>
        {
            new()
            {
                Function = new FunctionDefinition
                {
                    Name = "execute_plan",
                    Description = @"规则查询工具：查询计划执行器。把客人的规则问题编译成结构化查询计划，程序会执行实体定位、完整意图检索、正式读取和结构导航。relation 选择：
- explain：X 是什么 / 怎么结算 / 怎么做 / 有什么效果 / 有几个
- condition：能不能 X / X 有什么前提 / 什么限制（读取 condition+cost+target 及声明引用）
- cost：X 要支付什么 / 费用及适用范围
- target：X 作用于谁 / 目标约束
- ordering：X 之后是什么 / 先后顺序 / 什么时候结束 / 每轮怎么轮转
- boundary：X 不会发生什么 / 满了怎么办 / 上限多少
- flow：流程查询。scope=game 返回整局结构；scope=local 必须给 entity，返回目标、直接子步骤和必要父级顺序/循环/条件
- list：分页浏览概念目录（实体解析失败需要找概念时用）
- members：枚举某个类型或显式集合的可声明成员；complete/partial 必须区分，不能把 partial 当全集
- rule_lookup：保留明确对象身份，同时用完整 intent 检索相关规则节点，程序自动正式读取有界候选；读取候选不等于已确认对象/相关性
- compute：只调用现有确定性公式/查表能力；缺参数或公式不支持时明确 partial/unsupported
- identify：客人用外观/位置描述某物但你不确定是哪个概念时，把描述原文作为 entity，程序返回候选
query 字段语义：
- entity：查谁/什么，填概念 id、准确中文名、canonical_path 或对象描述；知道准确身份时直接用，不要求凭空猜 id。
- intent：针对 entity 你要知道什么，保留用户问题里的动作、场景和疑点。必须来自用户问题及有效上下文，不能写成你已经猜出的答案，也不能包含尚未证实的规则。
- relation：需要解释、条件、顺序、边界、流程、目录、成员等哪类规则信息。
- scope：flow 必填，local 或 game；其他 relation 省略。scope=local 且无 entity 是 invalid，不会退回整局。
- offset/limit：list/members 分页参数，limit 默认 50，最大 200。
- include：flow scope=local 时可写 children/position/conditions，声明需要的局部上下文。
单一疑点默认只发一个 query；只有确实需要不同事实时才拆多个 query，每个 query 写自己的 intent。不要把每个名词机械拆成 explain + condition。
实体解析分三层：精确命中直接返回规则事实；未精确命中时程序会结合完整 intent 返回 Status=unresolved + Candidates。候选描述只用于选择目标，不能当作已确认事实；请从候选中挑真正回答 intent 的概念，用准确 id 或准确中文名、保持原 relation 和 intent 重新发起计划。没有合适的候选再用 identify 或 list。若返回 Status=no_match，说明规则库未确认到该事实——请向客人说明未查到或请其换个问法，不要基于记忆补写规则。若结果被标记为 unresolved，不要直接下结论；确认后的正式事实才可用于回答。直接回答用户所问，不重复询问已知信息，不添加没有依据的现场状态；必要规则前提可简短说明。",

                    Parameters = JsonDocument.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "plan": {
                          "type": "object",
                          "properties": {
                            "queries": {
                              "type": "array",
                              "items": {
                                "type": "object",
                                "properties": {
                                  "relation": {
                                    "type": "string",
                                    "enum": ["explain", "condition", "cost", "target", "ordering", "boundary", "flow", "list", "members", "rule_lookup", "compute", "identify"]
                                  },
                                  "entity": {
                                    "type": "string",
                                    "description": "概念 id、准确中文名或对象描述（flow/list 可省略）"
                                  },
                                  "intent": {
                                    "type": "string",
                                    "description": "针对 entity 要知道的动作、场景或疑点，来自用户问题及有效上下文；不要写猜测的答案或未证实的规则。单 query 缺省时程序可用原问句回填，多 query 时请逐项填写。"
                                  },
                                  "scope": {
                                    "type": "string",
                                    "enum": ["local", "game"],
                                    "description": "flow 必填。scope=local 表示查询目标局部结构且必须给 entity；scope=game 表示整局流程且不应给 entity。"
                                  },
                                  "offset": {
                                    "type": "integer",
                                    "minimum": 0,
                                    "description": "list/members 分页偏移，默认 0。"
                                  },
                                  "limit": {
                                    "type": "integer",
                                    "minimum": 1,
                                    "maximum": 200,
                                    "description": "list/members 单页上限，默认 50。"
                                  },
                                  "include": {
                                    "type": "array",
                                    "items": { "type": "string" },
                                    "description": "flow scope=local 时要包含的局部上下文，例如 children、position、conditions。"
                                  },
                                  "continuation_handle": {
                                    "type": "string",
                                    "description": "可选。只能回传工具结果 omissions[].continuation.handle 中已登记的续读句柄；程序会绑定原 snapshot、目标和范围展开，不要自行构造 entity/offset。"
                                  }
                                },
                                "required": ["relation"]
                              }
                            }
                          },
                          "required": ["queries"]
                        }
                      },
                      "required": ["plan"]
                    }
                    """).RootElement
                }
            },
            new()
            {
                Function = new FunctionDefinition
                {
                    Name = "submit_answer",
                    Description = @"终态提交工具。回答已生成时用这一次模型调用同时提交最终文本，不额外增加检查轮次。
outcome 只能是 answer | clarify | unavailable | conversation。
- answer：必须给出本请求中实际交付过的 evidence_ids；程序会校验 evidence id 存在且当前请求已交付。引用只证明出处存在，不证明全文蕴含。
- clarify：可以无引用；请在 clarification.missing_kind 与 clarification.requested_information 说明唯一会改变答案的必要信息。不要为了清空 pending 反复追问。
- unavailable：必须给出 unavailable_reason=no_rule_coverage | unresolved_identity | budget_exhausted。
text 必须与 outcome 一致，简洁、TTS 友好。",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "outcome": {
                          "type": "string",
                          "enum": ["answer", "clarify", "unavailable", "conversation"]
                        },
                        "text": { "type": "string" },
                        "evidence_ids": {
                          "type": "array",
                          "items": { "type": "string" },
                          "description": "answer 时必填；只能引用本请求 evidence_catalog 中实际交付的 evidence_id。"
                        },
                        "clarification": {
                          "type": "object",
                          "properties": {
                            "missing_kind": { "type": "string" },
                            "requested_information": { "type": "string" }
                          },
                          "required": ["missing_kind", "requested_information"]
                        },
                        "unavailable_reason": {
                          "type": "string",
                          "enum": ["no_rule_coverage", "unresolved_identity", "budget_exhausted"]
                        }
                      },
                      "required": ["outcome", "text"]
                    }
                    """).RootElement
                }
            }
        };
    }
}
