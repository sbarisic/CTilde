# Language tour

This hosted example concentrates language features that are easy to miss in the larger programs:

- owner-relative immutable `[Embed]` data;
- Unicode `rune` literals and UTF-8 decode/encode;
- captureless and explicit-capture lambdas;
- user-defined arithmetic, equality, and ordering operators;
- abstract-class and interface dispatch, plus an `is` type test;
- nominal newtypes, packed and explicit aggregate layout, unions, and layout operators;
- `do` loops, compile-time `static assert`, target-selected `static if`, and `[NoRecursion]`.

Build and run it from the repository root:

```powershell
dotnet run --project .\CTilde.Cli -c Release -- --project .\examples\LanguageTour\ctilde.json --run
```

The manifest fixes x64 so its final `static if` output is deterministic. It otherwise uses the portable baseline CPU and precise floating-point profile.
