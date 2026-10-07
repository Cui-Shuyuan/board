# Animation Model Tests

Pure .NET tests for the Unity-independent animation runtime model:

- `AnimDefs.cs`
- `StateModel.cs`
- `TimelineModel.cs` / split `Timeline*.cs` files
- `Easing.cs`

They cover logical state replay, camera state, move/flip/shuffle clips,
overlay show/hide/modifiers, annotations, magnifiers and same-time compiled
clip ordering.

Run from the repository root:

```bash
dotnet run --project tests/AnimationModelTests
```

On WSL with a Windows `dotnet.exe`, run it from the repo (D: drive) and with a
Windows-accessible temp directory if your SDK needs one:

```bash
TMPDIR="$(pwd)/.tmp-dotnet" dotnet run --project tests/AnimationModelTests
```
