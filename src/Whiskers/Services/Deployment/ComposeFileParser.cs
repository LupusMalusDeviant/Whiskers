using Whiskers.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Whiskers.Services.Deployment;

public static class ComposeFileParser
{
    public static DeploymentValidationResult Parse(string yamlContent)
    {
        var result = new DeploymentValidationResult();

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var compose = deserializer.Deserialize<Dictionary<string, object>>(yamlContent);

            if (compose == null || !compose.ContainsKey("services"))
            {
                result.Errors.Add("No 'services' section found in compose file");
                return result;
            }

            var services = compose["services"] as Dictionary<object, object>;
            if (services == null)
            {
                result.Errors.Add("Invalid 'services' section format");
                return result;
            }

            foreach (var (serviceName, serviceDefObj) in services)
            {
                var name = serviceName.ToString()!;
                var serviceDef = serviceDefObj as Dictionary<object, object>;
                if (serviceDef == null)
                {
                    result.Errors.Add($"Service '{name}': invalid definition");
                    continue;
                }

                if (serviceDef.ContainsKey("build"))
                {
                    result.Errors.Add($"Service '{name}': 'build' directive not supported, use pre-built images only");
                    continue;
                }

                if (!serviceDef.TryGetValue("image", out var imageObj) || string.IsNullOrWhiteSpace(imageObj?.ToString()))
                {
                    result.Errors.Add($"Service '{name}': 'image' is required");
                    continue;
                }

                var request = new DeploymentRequest
                {
                    Image = imageObj.ToString()!,
                    ContainerName = serviceDef.TryGetValue("container_name", out var cn) ? cn?.ToString() : name
                };

                // Parse ports. Compose accepts "container", "host:container" and "ip:host:container"
                // (each with an optional "/proto" suffix). Never silently drop a form — a dropped port
                // means a stack that deploys "successfully" while publishing nothing.
                if (serviceDef.TryGetValue("ports", out var portsObj) && portsObj is List<object> ports)
                {
                    foreach (var port in ports)
                    {
                        var portStr = port.ToString()!;
                        var parts = portStr.Split(':');
                        switch (parts.Length)
                        {
                            case 1: // "80" — publish the container port on the same host port.
                            {
                                var p = parts[0].Split('/')[0];
                                request.PortMappings[p] = p;
                                break;
                            }
                            case 2: // "8080:80"
                                request.PortMappings[parts[0]] = parts[1].Split('/')[0];
                                break;
                            case 3: // "127.0.0.1:8080:80" — interface bind (loopback) + host + container.
                                request.PortMappings[parts[1]] = parts[2].Split('/')[0];
                                request.PortBindIps[parts[1]] = parts[0];
                                break;
                            default:
                                result.Errors.Add($"Service '{name}': port syntax '{portStr}' not supported");
                                break;
                        }
                    }
                }

                // Parse volumes
                if (serviceDef.TryGetValue("volumes", out var volsObj) && volsObj is List<object> vols)
                {
                    foreach (var vol in vols)
                    {
                        request.Volumes.Add(vol.ToString()!);
                    }
                }

                // Parse environment
                if (serviceDef.TryGetValue("environment", out var envObj))
                {
                    if (envObj is List<object> envList)
                    {
                        foreach (var env in envList)
                        {
                            var parts = env.ToString()!.Split('=', 2);
                            if (parts.Length == 2)
                                request.EnvironmentVars[parts[0]] = parts[1];
                        }
                    }
                    else if (envObj is Dictionary<object, object> envDict)
                    {
                        foreach (var (k, v) in envDict)
                        {
                            request.EnvironmentVars[k.ToString()!] = v?.ToString() ?? "";
                        }
                    }
                }

                // Parse restart policy
                if (serviceDef.TryGetValue("restart", out var restartObj))
                {
                    request.RestartPolicy = restartObj.ToString()!;
                }

                // Parse network
                if (serviceDef.TryGetValue("network_mode", out var netObj))
                {
                    request.NetworkName = netObj.ToString();
                }

                result.Services.Add(request);
            }

            result.IsValid = result.Errors.Count == 0 && result.Services.Count > 0;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"YAML parse error: {ex.Message}");
        }

        return result;
    }
}
