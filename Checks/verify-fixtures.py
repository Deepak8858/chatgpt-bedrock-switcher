"""Independently verify actual service outputs with Python's standard TOML parser."""
import json
import sys
import tomllib
from pathlib import Path

root = Path(sys.argv[1])
managed = {"model_provider", "model", "service_tier"}
results = []
for fixture in sorted(root.iterdir()):
    if not fixture.is_dir():
        continue
    def load(name):
        with (fixture / name).open("rb") as source:
            return tomllib.load(source)
    before, bedrock, restored = (load(name) for name in ("original.toml", "bedrock.toml", "restored.toml"))
    assert bedrock["model_provider"] == "amazon-bedrock-runtime", fixture
    assert bedrock["model"] == "global.openai.gpt-6-sol", fixture
    assert "service_tier" not in bedrock, fixture
    assert {k: v for k, v in before.items() if k not in managed} == {k: v for k, v in bedrock.items() if k not in managed}, fixture
    assert before == restored, fixture
    assert (fixture / "original.env").read_bytes() == (fixture / "restored.env").read_bytes(), fixture
    env = (fixture / "bedrock.env").read_text().splitlines()
    for line in ("AWS_PROFILE=default", "AWS_REGION=us-east-2", "AWS_DEFAULT_REGION=us-east-2", "UNRELATED=preserve"):
        assert env.count(line) == 1, (fixture, line)
    results.append({"fixture": fixture.name, "passed": True})
assert len(results) == 50, len(results)
report = {"parser": "Python tomllib (independent of Tomlyn)", "total": len(results), "failed": 0, "results": results}
(root.parent / "independent-toml-report.json").write_text(json.dumps(report, indent=2))
print(f"PASS: {len(results)}/50 fixtures verified with independent TOML parser; restored environment bytes match.")
