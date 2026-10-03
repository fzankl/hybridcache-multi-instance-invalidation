using Azure.Provisioning.RedisEnterprise;

var builder = DistributedApplication.CreateBuilder(args);

// Locally: a Redis container. Published to Azure: Azure Managed Redis, Entra ID only,
// plus a Container Apps environment (needed for the role assignment on the cache).
// One Redis for everything: L2 for all variants, and pub/sub for the FusionCache backplane.
builder.AddAzureContainerAppEnvironment("env");

var redis = builder.AddAzureManagedRedis("redis")
    .ConfigureInfrastructure(infrastructure =>
    {
        // The service default is OSSCluster. Written out so the policy is visible in the Bicep
        // and recorded with the results: the backplane relies on pub/sub.
        var database = infrastructure.GetProvisionableResources()
            .OfType<RedisEnterpriseDatabase>()
            .Single();
        database.ClusteringPolicy = RedisEnterpriseClusteringPolicy.OssCluster;
    })
    .RunAsContainer();

// "hybridcache" is Microsoft.Extensions.Caching.Hybrid. Azure rejects resource names that contain
// "microsoft" (ReservedResourceName), so the variant cannot carry that name.
// Two instances per variant, each with its own L1. Fixed ports locally, so the scenario can
// address A and B. In Azure, every instance is its own Container App with its own hostname.
(string Variant, int PortA)[] variants =
[
    ("hybridcache", 5101),
    ("fusioncache", 5201),
    ("fusioncache-backplane", 5301),
    ("fusioncache-tags", 5401)
];

foreach (var (variant, portA) in variants)
{
    foreach (var (instance, port) in new[] { ("a", portA), ("b", portA + 1) })
    {
        builder.AddProject<Projects.CacheInvalidation_Api>($"{variant}-{instance}", options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint(port: port)
            .WithExternalHttpEndpoints()
            .WithEnvironment("Cache__Variant", variant)
            .WithEnvironment("Cache__Instance", instance)
            .WithReference(redis)
            .WaitFor(redis)
            // Exactly one replica: scale-to-zero would drop the L1, scale-out would add a second one.
            .PublishAsAzureContainerApp((_, app) =>
            {
                app.Template.Scale.MinReplicas = 1;
                app.Template.Scale.MaxReplicas = 1;
            });
    }
}

// The symptom as users see it: two replicas behind one endpoint. After a remove,
// reads alternate between the old and the new value, depending on which replica answers.
builder.AddProject<Projects.CacheInvalidation_Api>("hybridcache-replicas", options => options.ExcludeLaunchProfile = true)
    .WithHttpEndpoint(port: 5111)
    .WithExternalHttpEndpoints()
    .WithReplicas(2)
    .WithEnvironment("Cache__Variant", "hybridcache")
    .WithEnvironment("Cache__Instance", "replica")
    .WithReference(redis)
    .WaitFor(redis)
    .PublishAsAzureContainerApp((_, app) =>
    {
        app.Template.Scale.MinReplicas = 2;
        app.Template.Scale.MaxReplicas = 2;
    });

builder.Build().Run();
