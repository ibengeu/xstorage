namespace OptiStorage.Client;

/// <summary>Configures the optional .NET client for one storage application.</summary>
public sealed record ObjectStoreClientOptions(string ServiceUrl, string AccessKeyId, string SecretAccessKey);
