# Roslyn contract sample

From the repository root:

```powershell
dotnet build .\src\Kernel\Boot\SingPlus.Boot\Samples\ManagedCap\ManagedCap.csproj
dotnet build .\src\Kernel\Boot\SingPlus.Boot\Samples\ManagedCap\ManagedCap.csproj -p:IncludeForbiddenApi=true
```

The first command must succeed. The second must fail with SING5001 on Guid.NewGuid. A normal build restores the positive sample. Open this project directly in Visual Studio to see the same diagnostics; it is not added to the solution because edits outside Boot are prohibited.

Generated protocol, manifest, client, dispatcher, response protocol and runtime sentry files appear under obj/generated. They are build artifacts, not a hand-maintained protocol replica. The contract uses synchronous bounded copied values because the current analyzer rejects ValueTask return types. No warning suppression or analyzer disabling is used.

ForbiddenApi.cs also contains ambient File.ReadAllText, but the current Sip/SipRegion source analyzer does not reject that API; post-build ManagedCap verification is a separate gate. Source analyzer success is not evidence of complete ManagedCap qualification. This sample generates binding code; it does not execute an admitted image or bypass internal trusted invocation context construction.
