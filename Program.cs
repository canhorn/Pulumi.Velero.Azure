using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Pulumi;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;

using AzureNative = Pulumi.AzureNative;
using AzureAD = Pulumi.AzureAD;
using KubernetesCore = Pulumi.Kubernetes.Core.V1;
using Helm = Pulumi.Kubernetes.Helm.V3;

return await Deployment.RunAsync(() =>
{
    var stackNameLowerCase = Deployment.Instance.StackName.ToLowerInvariant();

    var resourceGroup = new AzureNative.Resources.ResourceGroup(
        $"ehz-{stackNameLowerCase}-velero-backups"
    );

    var adApp = new AzureAD.Application(
        $"velero-backups-application",
        new AzureAD.ApplicationArgs { DisplayName = $"ehz-{stackNameLowerCase}-velero-backups" }
    );

    var adServicePrincipal = new AzureAD.ServicePrincipal(
        $"velero-service-principal",
        new AzureAD.ServicePrincipalArgs { ApplicationId = adApp.ApplicationId, }
    );

    var adSpPassword = new AzureAD.ServicePrincipalPassword(
        $"velero-service-principal-password",
        new AzureAD.ServicePrincipalPasswordArgs
        {
            ServicePrincipalId = adServicePrincipal.Id.Apply(async value =>
            {
                // Wait for propagation of new Service Principal
                await Task.Delay(2000);
                return value;
            }),
            EndDate = "2099-01-01T00:00:00Z",
        }
    );

    var adSpRoleAssignment = new AzureNative.Authorization.RoleAssignment(
        $"velero-role-assignment",
        new AzureNative.Authorization.RoleAssignmentArgs
        {
            RoleAssignmentName = "77e34fd9-ef53-421a-9a9d-4f2eba863269",
            PrincipalId = adServicePrincipal.Id,
            PrincipalType = AzureNative.Authorization.PrincipalType.ServicePrincipal,
            // Built-in Contributor Role Definition Id
            // - https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles
            RoleDefinitionId =
                "/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c",
            Scope = resourceGroup.Id,
        }
    );

    var veleroStorageAccount = new AzureNative.Storage.StorageAccount(
        "velero-storage-account",
        new AzureNative.Storage.StorageAccountArgs
        {
            ResourceGroupName = resourceGroup.Name,
            AccountName = $"ehz{stackNameLowerCase}velero",
            Kind = AzureNative.Storage.Kind.BlobStorage,
            AccessTier = AzureNative.Storage.AccessTier.Hot,
            EnableHttpsTrafficOnly = true,
            Sku = new AzureNative.Storage.Inputs.SkuArgs
            {
                Name = AzureNative.Storage.SkuName.Standard_GRS,
            },
            Encryption = new AzureNative.Storage.Inputs.EncryptionArgs
            {
                Services = new AzureNative.Storage.Inputs.EncryptionServicesArgs
                {
                    Blob = new AzureNative.Storage.Inputs.EncryptionServiceArgs { Enabled = true, },
                },
                KeySource = AzureNative.Storage.KeySource.Microsoft_Storage,
            },
        }
    );

    var veleroStorageContainer = new AzureNative.Storage.BlobContainer(
        "velero-storage-container",
        new AzureNative.Storage.BlobContainerArgs
        {
            ResourceGroupName = resourceGroup.Name,
            AccountName = veleroStorageAccount.Name,
            ContainerName = $"ehz{stackNameLowerCase}velerobackups",
            PublicAccess = AzureNative.Storage.PublicAccess.None,
        }
    );

    var veleroVersion = "4.1.4";
    var veleroBackupsNamespace = new KubernetesCore.Namespace(
        "velero-backups-namespace",
        new NamespaceArgs { Metadata = new ObjectMetaArgs { Name = "velero-backups", } }
    );

    var veleroRelease = new Helm.Release(
        "velero-backups-release",
        new ReleaseArgs
        {
            Name = "velero-release",
            Namespace = veleroBackupsNamespace.Metadata.Apply(a => a.Name),
            Chart = "velero",
            RepositoryOpts = new RepositoryOptsArgs
            {
                Repo = "https://vmware-tanzu.github.io/helm-charts"
            },
            Version = veleroVersion,
            Values = new InputMap<object>
            {
                ["snapshotsEnabled"] = false,
                ["image"] = new Dictionary<string, object>
                {
                    ["repository"] = "velero/velero",
                    ["pullPolicy"] = "Always",
                },
                ["configuration"] = new Dictionary<string, object>
                {
                    ["backupStorageLocation"] = new InputList<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["provider"] = "azure",
                            ["bucket"] = veleroStorageContainer.Name,
                            ["config"] = new Dictionary<string, object>
                            {
                                ["resourceGroup"] = resourceGroup.Name,
                                ["storageAccount"] = veleroStorageAccount.Name,
                            },
                        }
                    },
                    ["volumeSnapshotLocation"] = new InputList<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["provider"] = "azure",
                            ["config"] = new Dictionary<string, object>
                            {
                                ["apiTimeout"] = "5m",
                                ["resourceGroup"] = resourceGroup.Name,
                                ["storageAccount"] = veleroStorageAccount.Name,
                            },
                        },
                    },
                },
                ["credentials"] = new Dictionary<string, object>
                {
                    ["secretContents"] = new Dictionary<string, object>
                    {
                        ["cloud"] = Output
                            .Tuple(
                                adServicePrincipal.ApplicationId,
                                adSpPassword.Value,
                                resourceGroup.Name
                            )
                            .Apply(async values =>
                            {
                                var (applicationId, adSpPassword, resourceGroupName) = values;
                                var azureConfig =
                                    await AzureNative.Authorization.GetClientConfig.InvokeAsync();

                                var json = new Dictionary<string, object>
                                {
                                    ["AZURE_SUBSCRIPTION_ID"] = azureConfig.SubscriptionId,
                                    ["AZURE_TENANT_ID"] = azureConfig.TenantId,
                                    ["AZURE_CLIENT_ID"] = applicationId,
                                    ["AZURE_CLIENT_SECRET"] = adSpPassword,
                                    ["AZURE_RESOURCE_GROUP"] = resourceGroupName,
                                    ["AZURE_CLOUD_NAME"] = "AzurePublicCloud",
                                };
                                var cloudSecrets = string.Join(
                                    Environment.NewLine,
                                    json.Select(kv => $"{kv.Key}={kv.Value}").ToArray()
                                );

                                return cloudSecrets;
                            })
                    },
                },
                ["initContainers"] = new InputList<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["name"] = "velero-plugin-for-microsoft-azure",
                        ["image"] = $"velero/velero-plugin-for-microsoft-azure:master",
                        ["volumeMounts"] = new InputList<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["name"] = "plugins",
                                ["mountPath"] = "/target",
                            },
                        },
                    },
                },
                ["schedules"] = new Dictionary<string, object>
                {
                    ["every-6-hours"] = new Dictionary<string, object>
                    {
                        ["disabled"] = false,
                        // Runs every 6 hours
                        ["schedule"] = "0 */6 * * *",
                        ["template"] = new Dictionary<string, object>
                        {
                            // Keeps the backup for 7 days
                            ["ttl"] = "168h0m0s",
                            ["storageLocation"] = "default",
                        },
                    },
                },
            }
        }
    );

    // Export outputs here
    return new Dictionary<string, object?>
    {
        ["AzureResourceGroupName"] = resourceGroup.Name,
        ["AzureADApplicationId"] = adApp.ApplicationId,
        ["AzureADServicePrincipalId"] = adServicePrincipal.Id,
        ["AzureADPassword"] = adSpPassword.Value.Apply(Output.CreateSecret),
        ["VeleroStorageAccountName"] = veleroStorageAccount.Name,
        ["VeleroStorageContainerName"] = veleroStorageContainer.Name,
    };
});
