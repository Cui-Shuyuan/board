"""Attribute frozen chat runs to API log intervals (no new model requests).

LLM intervals include remote queue/network/prefill/reasoning/output and local
response deserialization. They are NOT measurements of reasoning time alone.
Legacy logs have millisecond resolution and no response-body completion event;
the first subsequent orchestrator log is the observed completion boundary.
"""
import argparse
import collections
import datetime as dt
import json
import re
import statistics
from pathlib import Path


HEADER = re.compile(r'^(\d\d:\d\d:\d\d\.\d+) \[([^]]+)\] (?:INFO|WARN|ERROR|DEBUG) (.*)$')


def millis(value):
    h, m, s = value.split(':')
    return (int(h) * 3600 + int(m) * 60 + float(s)) * 1000


def parse_log(path):
    requests = {}
    current = None
    for line in path.read_text(encoding='utf-8-sig', errors='replace').splitlines():
        match = HEADER.match(line)
        if match:
            timestamp, trace, message = match.groups()
            current = [millis(timestamp), message]
            requests.setdefault(trace, []).append(current)
        elif current is not None:
            current[1] += '\n' + line
    parsed = []
    for trace, events in requests.items():
        starts = [(t, re.search(r'\[Chat\] game: (.*?), question: (.*), count: \d+', msg))
                  for t, msg in events]
        starts = [(t, m) for t, m in starts if m]
        if len(starts) != 1:
            continue
        start, match = starts[0]
        record = dict(trace=trace, game=match[1], question=match[2], start_ms=start,
                      llm_ms=0, tool_ms=0, llm_calls=0, tool_calls=0,
                      correction_ms=0, corrections=0, protocol_corrections=0,
                      reasoning_chars=0, reasoning=[], calls=[])
        llm_start = tool_start = None
        is_correction = False
        for t, msg in events:
            orchestrator = 'ChatOrchestratorService ' in msg
            if llm_start is not None and orchestrator:
                duration = t - llm_start
                record['llm_ms'] += duration
                record['calls'].append(dict(ms=round(duration, 3), completion=msg.splitlines()[0]))
                if is_correction:
                    record['correction_ms'] += duration
                    is_correction = False
                llm_start = None
            if 'ILLMService.LogicalHandler Start processing HTTP request POST' in msg:
                if llm_start is not None:
                    raise ValueError(f'Unclosed LLM call: {trace}')
                llm_start = t
                record['llm_calls'] += 1
            if orchestrator and 'answer constraint correction:' in msg:
                record['corrections'] += 1
                is_correction = True
            if orchestrator and 'blocked premature final answer:' in msg:
                record['protocol_corrections'] += 1
            if orchestrator and re.search(r'Round \d+ reasoning:', msg):
                reasoning = msg.split('\n', 1)[1] if '\n' in msg else ''
                record['reasoning'].append(reasoning)
                record['reasoning_chars'] += len(reasoning)
            if orchestrator and re.search(r'Round \d+ tool calls:', msg):
                tool_start = t
            if orchestrator and 'Tool execute_plan result:' in msg:
                if tool_start is None:
                    raise ValueError(f'Missing tool start: {trace}')
                record['tool_ms'] += t - tool_start
                record['tool_calls'] += 1
                tool_start = t  # subsequent sequential calls in the same round
            if orchestrator and ('final reply (' in msg or 'final reply after stop=' in msg):
                record['server_ms'] = t - start
        if 'server_ms' in record and llm_start is None:
            record['residual_ms'] = record['server_ms'] - record['llm_ms'] - record['tool_ms']
            parsed.append(record)
    return parsed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runs', type=Path, required=True)
    parser.add_argument('--old-log', type=Path, required=True)
    parser.add_argument('--new-log', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    logs = {v: parse_log(path) for v, path in [('old', args.old_log), ('new', args.new_log)]}
    rows = []
    used = set()
    for line in args.runs.read_text(encoding='utf-8').splitlines():
        run = json.loads(line)
        stamp = dt.datetime.fromisoformat(run['started_at']).astimezone(dt.timezone(dt.timedelta(hours=8)))
        start_ms = ((stamp.hour * 60 + stamp.minute) * 60 + stamp.second) * 1000 + stamp.microsecond / 1000
        candidates = [r for r in logs[run['version']] if r['question'] == run['question'] and r['game'] == run['game']
                      and abs(r['start_ms'] - start_ms) < 3000 and (run['version'], r['trace']) not in used]
        if len(candidates) != 1:
            raise ValueError(f"Expected unique log match for {run['run_id']}, found {len(candidates)}")
        row = dict(candidates[0], version=run['version'], run_id=run['run_id'], latency_ms=run['latency_ms'])
        used.add((row['version'], row['trace']))
        rows.append(row)
    summary = {}
    for version in logs:
        subset = [r for r in rows if r['version'] == version]
        metrics = ['latency_ms', 'server_ms', 'llm_ms', 'tool_ms', 'residual_ms', 'llm_calls',
                   'tool_calls', 'correction_ms', 'reasoning_chars']
        summary[version] = dict(n=len(subset), means={k: round(statistics.mean(r[k] for r in subset), 3) for k in metrics},
                               medians={k: round(statistics.median(r[k] for r in subset), 3) for k in metrics},
                               llm_call_distribution=dict(collections.Counter(r['llm_calls'] for r in subset)),
                               correction_requests=sum(r['corrections'] > 0 for r in subset),
                               protocol_correction_requests=sum(r['protocol_corrections'] > 0 for r in subset),
                               min_residual_ms=min(r['residual_ms'] for r in subset))
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
    (args.out / 'requests.jsonl').write_text(''.join(json.dumps(r, ensure_ascii=False) + '\n' for r in rows), encoding='utf-8')
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
