# derive-gold

An independent Python re-derivation of the gold answers, written in a different style from the C# oracles
(`src/FhirAgentBench/Oracles/`). It satisfies design spec §7's "second derivation": two implementations,
written separately from the oracle semantics in `docs/questions/oracle-semantics.md`, must agree.

It reads the questions listed in a question-set file (default `question-sets/v1-100.txt`), loads the pinned
corpus, and evaluates each question's oracle. Questions whose oracle it does not implement are skipped and
counted. It needs only the Python standard library.

```
# print golds to stdout
python -I tools/derive-gold/derive_gold.py

# write golds outside the repo
python -I tools/derive-gold/derive_gold.py --out "$TMPDIR/gold-python.json"

# compare against the C# golds
dotnet run --project src/FhirAgentBench.Cli -- admit --corpus corpora/synthea-sample-v1 --set question-sets/v1-100.txt --show > "$TMPDIR/csharp.txt"
python -I tools/derive-gold/derive_gold.py --compare "$TMPDIR/csharp.txt"
```

`--compare` ignores set order, allows numeric differences within each question's declared tolerance (or 1e-6),
and treats `{system, code}` objects and bare code strings as equal. It prints a match/mismatch summary and
exits non-zero on any mismatch.

Its output is gold. Never commit it or copy it into a run workspace; there is deliberately no default `--out`.
