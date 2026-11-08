# Services/Vpn/Providers

The mesh VPN providers behind `IVpnProvider` (see the parent folder). The provider is chosen by configuration;
the default is `none`.

| File | What |
|---|---|
| `NoopVpnProvider.cs` | Default: the mesh is managed outside the container (host or sidecar) and Whiskers only uses the resulting mesh IPs. Keeps the app image free of a VPN daemon, which a distroless image needs. |
| `TailscaleVpnProvider.cs` | Ensures `tailscaled` runs inside the container, then `tailscale up` with state under the configured StateDir. R1-43: every argument is its own argv element, hostname and login server are validated first (`VpnArguments`), and the auth key is passed as `--auth-key=file:<path>`, not as a value. |
| `NetbirdVpnProvider.cs` | NetBird: makes sure the netbird service runs, then `netbird up` (optionally against a self-hosted management URL) with validated arguments; the setup key goes as `--setup-key-file=<path>` from a 0600 file deleted when the call returns. Degrades gracefully when the binary is absent; no StateDir setting. |
