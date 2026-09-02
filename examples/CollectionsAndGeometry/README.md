# Collections and geometry

This hosted example exercises the generic value helpers, array algorithms, mutable collections, versioned enumeration, and scalar-layout geometry APIs in the common C~ standard library.

The collection section uses `List<T>`, `Stack<T>`, `Queue<T>`, `Map<TKey,TValue>`, and `Set<T>`. It also deliberately mutates a list after creating an enumerator and catches the resulting `InvalidOperationException`. The geometry section composes two-dimensional and three-dimensional transforms with `Vec2`, `Vec4`, `Matrix3x2`, `Matrix4x4`, and `Quaternion`.

Build and run it from the repository root:

```powershell
dotnet run --project .\CTilde.Cli -- --project .\examples\CollectionsAndGeometry\ctilde.json --build
.\examples\CollectionsAndGeometry\build\CollectionsAndGeometry.exe
```

Successful output contains five `True` results:

```text
generic values: True
array algorithms: True
collections: True
version guard: True
geometry: True
```
