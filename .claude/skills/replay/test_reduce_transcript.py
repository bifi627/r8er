import os
import tempfile
import unittest

from reduce_transcript import reduce_records, reduce_file


class TestReduceRecords(unittest.TestCase):
    def test_keeps_dialogue_drops_noise(self):
        records = [
            {"type": "user", "timestamp": "2026-06-10T10:00:00Z", "sessionId": "s1",
             "gitBranch": "stage",
             "message": {"role": "user", "content": "always use block braces"}},
            {"type": "assistant", "timestamp": "2026-06-10T10:00:05Z", "sessionId": "s1",
             "message": {"role": "assistant", "content": [
                 {"type": "thinking", "thinking": "user wants braces"},
                 {"type": "text", "text": "Got it, I'll use block braces."},
                 {"type": "tool_use", "name": "Edit", "input": {"x": 1}}]}},
            # tool-result echo recorded as a 'user' record -> dropped
            {"type": "user", "timestamp": "2026-06-10T10:00:10Z", "sessionId": "s1",
             "toolUseResult": {"stdout": "..."},
             "message": {"role": "user",
                         "content": [{"type": "tool_result", "content": "big output"}]}},
            {"type": "system", "subtype": "hook", "content": None},
            {"type": "file-history-snapshot", "snapshot": {}},
            {"type": "ai-title"},
            # sidechain (subagent) noise -> dropped
            {"type": "assistant", "isSidechain": True, "sessionId": "s1",
             "message": {"role": "assistant",
                         "content": [{"type": "text", "text": "sidechain noise"}]}},
            # assistant with only tool_use -> empty text -> dropped
            {"type": "assistant", "sessionId": "s1",
             "message": {"role": "assistant",
                         "content": [{"type": "tool_use", "name": "Read", "input": {}}]}},
        ]
        self.assertEqual(reduce_records(records), [
            {"role": "user", "text": "always use block braces",
             "timestamp": "2026-06-10T10:00:00Z", "session_id": "s1", "git_branch": "stage"},
            {"role": "assistant", "text": "Got it, I'll use block braces.",
             "timestamp": "2026-06-10T10:00:05Z", "session_id": "s1", "git_branch": None},
        ])

    def test_drops_command_and_meta_noise(self):
        records = [
            # isMeta injected caveat -> dropped
            {"type": "user", "sessionId": "s1", "isMeta": True,
             "message": {"role": "user",
                         "content": "<local-command-caveat>Caveat: ...</local-command-caveat>"}},
            # slash-command scaffolding -> dropped
            {"type": "user", "sessionId": "s1",
             "message": {"role": "user",
                         "content": "<command-name>/plugin</command-name> <command-message>p</command-message>"}},
            {"type": "user", "sessionId": "s1",
             "message": {"role": "user",
                         "content": "<local-command-stdout>Session renamed</local-command-stdout>"}},
            # genuine turn with an injected reminder appended -> keep human text, strip reminder
            {"type": "user", "sessionId": "s1", "timestamp": "t", "gitBranch": "stage",
             "message": {"role": "user",
                         "content": "use kebab-case for files\n"
                                    "<system-reminder>background note</system-reminder>"}},
            # only a reminder -> empty after strip -> dropped
            {"type": "user", "sessionId": "s1",
             "message": {"role": "user",
                         "content": "<system-reminder>just context</system-reminder>"}},
        ]
        self.assertEqual(reduce_records(records), [
            {"role": "user", "text": "use kebab-case for files",
             "timestamp": "t", "session_id": "s1", "git_branch": "stage"},
        ])


class TestReduceFile(unittest.TestCase):
    def _write(self, text):
        fd, path = tempfile.mkstemp(suffix=".jsonl")
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(text)
        self.addCleanup(os.remove, path)
        return path

    def test_skips_blank_and_malformed_lines(self):
        path = self._write(
            '{"type":"user","sessionId":"s1","message":{"role":"user","content":"hi"}}\n'
            "\n"
            "{ this is not json }\n"
            '{"type":"assistant","sessionId":"s1","message":{"role":"assistant",'
            '"content":[{"type":"text","text":"hello"}]}}\n'
        )
        result = reduce_file(path)
        self.assertEqual([r["text"] for r in result], ["hi", "hello"])

    def test_empty_file_returns_empty_list(self):
        self.assertEqual(reduce_file(self._write("")), [])


if __name__ == "__main__":
    unittest.main()
