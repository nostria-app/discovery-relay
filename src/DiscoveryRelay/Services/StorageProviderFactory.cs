using DiscoveryRelay.Options;
using Microsoft.Extensions.Options;

namespace DiscoveryRelay.Services;

/// <summary>
/// Factory service for creating storage providers
/// </summary>
public class StorageProviderFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<StorageOptions> _storageOptions;
    private readonly ILogger<StorageProviderFactory> _logger;

    public StorageProviderFactory(
        IServiceProvider serviceProvider,
        IOptions<StorageOptions> storageOptions,
        ILogger<StorageProviderFactory> logger)
    {
        _serviceProvider = serviceProvider;
        _storageOptions = storageOptions;
        _logger = logger;
    }    /// <summary>
         /// Creates an instance of the configured storage provider
         /// </summary>
    public IStorageProvider CreateProvider()
    {
        IStorageProvider provider;

        switch (_storageOptions.Value.Provider?.ToLowerInvariant())
        {
            case "azureblob":
                provider = _serviceProvider.GetRequiredService<AzureBlobStorageProvider>();
                break;
            default:
                provider = _serviceProvider.GetRequiredService<LmdbStorageProvider>();
                break;
        }

        _logger.LogInformation("Using storage provider: {Provider}", provider.GetType().Name);
        return provider;
    }
}
