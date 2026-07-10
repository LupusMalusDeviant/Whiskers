namespace Whiskers.Modules.HelloWorld;

/// <summary>A trivial service, registered by <see cref="HelloWorldModule.ConfigureServices"/>, to show how a
/// module contributes its own DI services. In a real module the service + interface live under
/// <c>Services/&lt;Area&gt;/</c> and the module just registers them; this example keeps everything in one
/// folder for readability.</summary>
public interface IHelloWorldGreeter
{
    string Greet();
}

public sealed class HelloWorldGreeter : IHelloWorldGreeter
{
    public string Greet() => "👋 Hello from the HelloWorld module — the RoadToSAP module-system example.";
}
