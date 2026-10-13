# Third-party notices

Stellaegis.Documents is licensed under the MIT License (see [LICENSE](LICENSE)).

Its packages contain no third-party code. They reference the packages below, which NuGet installs
alongside them, each under its own license.

| Component | Used by | License (SPDX) | Project |
| --- | --- | --- | --- |
| Stellaegis.Primitives | all packages | MIT | <https://github.com/Stellaegis/Stellaegis.Primitives> |
| Stellaegis.Primitives.Data | `Stellaegis.Documents.PostgreSQL` | MIT | <https://github.com/Stellaegis/Stellaegis.Primitives> |
| Stellaegis.Blobs | all packages | MIT | <https://github.com/Stellaegis/Stellaegis.Blobs> |
| Dapper | `Stellaegis.Documents.PostgreSQL` | Apache-2.0 | <https://github.com/DapperLib/Dapper> |
| Npgsql | `Stellaegis.Documents.PostgreSQL` | PostgreSQL | <https://github.com/npgsql/npgsql> |
| Microsoft.Extensions.\*, ASP.NET Core | all packages | MIT | <https://github.com/dotnet> |

The PostgreSQL store relies on the `pgcrypto` extension, part of PostgreSQL itself (PostgreSQL
License), which the schema script enables.

Test-only dependencies (xUnit, Microsoft.NET.Test.Sdk, ASP.NET Core TestHost, Stellaegis.Blobs.FileSystem)
are not part of any package and are not listed here.
