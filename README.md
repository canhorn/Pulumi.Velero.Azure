# Pulumi.Velero.Azure

An demo of using Velero to backup using the Azure Storage Provider. The locally configured Kubernetes cluster accessible from `kubectl` is used to create the Velero resources. The Azure resources are created using Pulumi.

## Usage

Below is a quick start guide to get you up and running with this example. For more detailed instructions, please see the [Pulumi Azure Provider documentation](https://www.pulumi.com/docs/intro/cloud-providers/azure/setup/) for more details on getting Pulumi setup with Azure and Kubernetes.

### Setup

- Install [Pulumi](https://www.pulumi.com/docs/get-started/install/)
- (Optional) Install [Velero](https://velero.io/docs/)
    - This is only needed if you want to use Velero to setup extra out of automation backups.
- Install [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli?view=azure-cli-latest)
- Install [Kubectl](https://kubernetes.io/docs/tasks/tools/install-kubectl/)
- Install [Helm](https://helm.sh/docs/intro/install/)
- Install [.NET](https://dotnet.microsoft.com/download)

### Running the Example

```bash
# Login to Azure, this is needed to create the storage account where the backups will be stored.
az login

# Login Pulumi, this is needed to track the state of the resources created.
pulumi login

# Pulumi Update, this will create the resources in Azure and in Kubernetes.
pulumi up
```
