using k8s;
using k8s.KubeConfigModels;

namespace Whiskers.Tests;

/// <summary>Regression guard for the vault-kubeconfig path (Track B.2): KubernetesClientCache
/// parses the kubeconfig IN-MEMORY via <see cref="KubernetesYaml.LoadFromString{T}"/>, which rides
/// on YamlDotNet — a YamlDotNet major bump that breaks KubernetesClient's deserialization would
/// otherwise only surface at runtime against a live cluster.</summary>
public class KubeconfigYamlParsingTests
{
    private const string SampleKubeconfig = """
        apiVersion: v1
        kind: Config
        clusters:
          - name: test-cluster
            cluster:
              server: https://10.0.0.1:6443
              certificate-authority-data: dGVzdA==
        users:
          - name: test-user
            user:
              token: abc123
        contexts:
          - name: test
            context:
              cluster: test-cluster
              user: test-user
        current-context: test
        """;

    [Fact]
    public void Kubeconfig_yaml_parses_via_KubernetesYaml()
    {
        var cfg = KubernetesYaml.LoadFromString<K8SConfiguration>(SampleKubeconfig);
        Assert.Equal("test", cfg.CurrentContext);
        Assert.Single(cfg.Clusters);
        Assert.Equal("https://10.0.0.1:6443", cfg.Clusters.First().ClusterEndpoint.Server);
        Assert.Equal("abc123", cfg.Users.First().UserCredentials.Token);
    }

    [Fact]
    public void Client_configuration_builds_from_the_parsed_object_in_memory()
    {
        var cfg = KubernetesYaml.LoadFromString<K8SConfiguration>(SampleKubeconfig);
        var clientConfig = KubernetesClientConfiguration.BuildConfigFromConfigObject(cfg);
        Assert.Equal("https://10.0.0.1:6443", clientConfig.Host);
        Assert.Equal("abc123", clientConfig.AccessToken);
    }
}
