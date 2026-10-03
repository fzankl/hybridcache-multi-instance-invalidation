# Raw output of the Azure run, 29 September 2026

The output of `scenario/run.cs` and `scenario/replicas.cs` for the Azure values of `hybridcache`, `fusioncache` and `fusioncache-backplane` in the results table. The `fusioncache-tags` variant did not exist in this run; its values are in [azure-2026-10-02](../azure-2026-10-02/README.md). The local runs are not stored here: you can reproduce them in about six minutes with `scenario/matrix.cs`.

These files apply to the versions and setup below, not necessarily to the current state of the repository.

|                |                                                                                                               |
|----------------|---------------------------------------------------------------------------------------------------------------|
| Packages       | `Microsoft.Extensions.Caching.Hybrid` 10.10.0, `ZiggyCreatures.FusionCache` 2.9.0                             |
| Apps           | Azure Container Apps, West US, deployed with `aspire deploy` (Aspire 13.4.6)                                  |
| Instances      | one replica per instance, two for `hybridcache-replicas`                                                      |
| Redis          | Azure Managed Redis, `Balanced_B0`, Redis 7.4, clustering policy `OSSCluster`, Entra ID, access keys disabled |
| Cache settings | `LocalCacheExpiration` 10 s, `Expiration` 60 s                                                                |
| Scenario       | ran on a machine in Germany, round trip to each instance 156 to 166 ms                                        |
| Runs           | one per case                                                                                                  |

| File                                                   | Content                                                   |
|--------------------------------------------------------|-----------------------------------------------------------|
| [hybridcache.txt](hybridcache.txt)                     | eight cases, Microsoft's implementation                   |
| [fusioncache.txt](fusioncache.txt)                     | eight cases, FusionCache without a backplane              |
| [fusioncache-backplane.txt](fusioncache-backplane.txt) | eight cases, FusionCache with the Redis backplane         |
| [hybridcache-replicas.txt](hybridcache-replicas.txt)   | two replicas behind one ingress, remove by key and by tag |

The first line of each file describes the environment. Everything after it is the output of the run, reformatted to match the layout the scenarios print today: one line per Redis call and per key, with decimal points instead of commas. Apart from that formatting, the recorded values are unchanged.

The resource group was deleted after the run, so the hostnames in the files no longer resolve.
