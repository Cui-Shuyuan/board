import tempfile
import unittest
from pathlib import Path

from analyze_chat_latency import parse_log


class LogLatencyTests(unittest.TestCase):
    def parse(self, lines):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'api.log'
            path.write_text('\n'.join(lines), encoding='utf-8')
            return parse_log(path)

    def test_headers_are_not_model_completion_and_traces_are_isolated(self):
        rows = self.parse([
            '12:00:00.000 [a] INFO ChatOrchestratorService [Chat] game: toy, question: 怎么做, count: 1',
            '12:00:00.010 [a] INFO HttpClient.ILLMService.LogicalHandler Start processing HTTP request POST endpoint',
            '12:00:00.020 [b] INFO HttpClient.ILLMService.LogicalHandler Start processing HTTP request POST endpoint',
            '12:00:00.030 [a] INFO HttpClient.ILLMService.LogicalHandler End processing HTTP request after 20ms - 200',
            '12:00:01.010 [a] INFO ChatOrchestratorService [Chat] Game toy, Round 1 reasoning:',
            'Choose a rule.',
            '12:00:01.020 [a] INFO ChatOrchestratorService [Chat] Game toy, Round 1 tool calls:',
            '12:00:01.120 [a] INFO ChatOrchestratorService [Chat] Game toy, Tool execute_plan result:',
            '{}',
            '12:00:01.130 [a] INFO HttpClient.ILLMService.LogicalHandler Start processing HTTP request POST endpoint',
            '12:00:02.130 [a] INFO ChatOrchestratorService [Chat] Game toy, Round 2 final reply (2130ms, tier1): 完成',
        ])
        self.assertEqual(1, len(rows))
        row = rows[0]
        self.assertEqual(2, row['llm_calls'])
        self.assertEqual(2000, row['llm_ms'])
        self.assertEqual(100, row['tool_ms'])
        self.assertEqual(30, row['residual_ms'])
        self.assertEqual(['Choose a rule.'], row['reasoning'])

    def test_answer_rewrite_is_a_separate_model_interval(self):
        rows = self.parse([
            '12:00:00.000 [a] INFO ChatOrchestratorService [Chat] game: toy, question: 怎么做, count: 1',
            '12:00:00.000 [a] INFO HttpClient.ILLMService.LogicalHandler Start processing HTTP request POST endpoint',
            '12:00:01.000 [a] WARN ChatOrchestratorService [Chat] Game toy, answer constraint correction: omitted=True',
            '12:00:01.000 [a] INFO HttpClient.ILLMService.LogicalHandler Start processing HTTP request POST endpoint',
            '12:00:03.000 [a] INFO ChatOrchestratorService [Chat] evidence summary',
            '12:00:03.001 [a] INFO ChatOrchestratorService [Chat] Game toy, Round 1 final reply (3001ms, tier1): 完成',
        ])
        self.assertEqual(3000, rows[0]['llm_ms'])
        self.assertEqual(2000, rows[0]['correction_ms'])
        self.assertEqual(1, rows[0]['corrections'])


if __name__ == '__main__':
    unittest.main()
