# Raw output of the Azure run, 2 October 2026

The output of `scenario/matrix.cs` and `scenario/reads.cs` against the second deployment, for all four variants and the replica scenario. The results table takes the Azure values of `fusioncache-tags` from this run. For the other three variants it shows the [run of 29 September](../azure-2026-09-29/README.md); the values here are within 0.3 s of those.

These files are valid for the versions and the setup below, not for whatever the repository uses today.

|                |                                                                                                               |
|----------------|---------------------------------------------------------------------------------------------------------------|
| Packages       | `Microsoft.Extensions.Caching.Hybrid` 10.10.0, `ZiggyCreatures.FusionCache` 2.9.0                             |
| Apps           | Azure Container Apps, West US, deployed with `aspire deploy` (Aspire 13.4.6)                                  |
| Instances      | one replica per instance, two for `hybridcache-replicas`                                                      |
| Redis          | Azure Managed Redis, `Balanced_B0`, Redis 7.4, clustering policy `OSSCluster`, Entra ID, access keys disabled |
| Cache settings | `LocalCacheExpiration` 10 s, `Expiration` 60 s, for `fusioncache-tags` tag markers in L1 for 5 s              |
| Scenario       | ran on a machine in Germany, round trip to each instance 155 to 199 ms                                        |
| Runs           | one per case                                                                                                  |

| File                                                   | Content                                                        |
|--------------------------------------------------------|----------------------------------------------------------------|
| [hybridcache.txt](hybridcache.txt)                     | eight cases, Microsoft's implementation                        |
| [fusioncache.txt](fusioncache.txt)                     | eight cases, FusionCache without a backplane                   |
| [fusioncache-backplane.txt](fusioncache-backplane.txt) | eight cases, FusionCache with the Redis backplane              |
| [fusioncache-tags.txt](fusioncache-tags.txt)           | eight cases, FusionCache without a backplane, tag markers 5 s  |
| [hybridcache-replicas.txt](hybridcache-replicas.txt)   | two replicas behind one ingress, remove by key and by tag      |
| [reads.txt](reads.txt)                                 | `scenario/reads.cs` for all four variants: L2 calls, no remove |

The first line of each file describes the environment. Everything after it is the output of the run, as `scenario/matrix.cs` wrote it. The resource group was deleted after the run, so the hostnames in the files no longer resolve.
