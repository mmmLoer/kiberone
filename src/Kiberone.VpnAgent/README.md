# Kiberone.VpnAgent

Windows Service: WireGuard tunnel via official [embeddable-dll-service](https://git.zx2c4.com/wireguard-windows/about/embeddable-dll-service/README.md) (`tunnel.dll` + `wireguard.dll`) and HTTP control API for the classroom router.

## Stack

| Piece | Detail |
|-------|--------|
| VPN | `tunnel.dll` → `WireGuardTunnelService` (same EXE `/service conf`) |
| Server | Existing **wg1** `80.90.188.85:51821`, peers `10.200.0.x` |
| API | Kestrel `0.0.0.0:9777` |
| Control | Router → `POST http://PC_IP:9777/v1/connect` |

## API

| Method | Path | Auth | Action |
|--------|------|------|--------|
| `GET` | `/health` or `/v1/health` | no | liveness |
| `GET` | `/v1/status` | `X-Vpn-Token` | up/down |
| `POST` | `/v1/connect` | `X-Vpn-Token` | start tunnel from local `peer.conf` |
| `POST` | `/v1/disconnect` | `X-Vpn-Token` | stop tunnel |

Aliases without `/v1` also work: `/status`, `/connect`, `/disconnect`.

Private keys stay on disk — never sent in HTTP.

## One-time install (admin)

1. Build/publish agent and place `tunnel.dll` + `wireguard.dll` next to the EXE:

```powershell
.\scripts\publish-vpn-agent.ps1
# Build tunnel.dll from wireguard-windows\embeddable-dll-service\build.bat (amd64)
# Download wireguard.dll from https://download.wireguard.com/wireguard-nt/
# Copy both into dist\VpnAgent-win-x64\
```

2. Install service + peer config:

```powershell
.\scripts\install-vpn-agent.ps1 `
  -SourceDir "D:\KIBERone-Classroom\dist\VpnAgent-win-x64" `
  -ApiToken "long-random-secret" `
  -PeerConf "C:\path\to\pc05.conf" `
  -AllowedRemoteAddresses "192.168.1.1"
```

Creates:

- Service `KiberoneVpnAgent` (auto-start, LocalSystem)
- Files under `C:\Program Files\KIBERone\VpnAgent`
- Config `C:\ProgramData\KIBERone\VpnAgent\peer.conf` (ACL: SYSTEM + Administrators)
- Firewall inbound TCP 9777

3. Daily (router, no admin on student session):

```bash
curl -X POST -H "X-Vpn-Token: SECRET" http://192.168.1.55:9777/v1/connect
curl -H "X-Vpn-Token: SECRET" http://192.168.1.55:9777/v1/status
curl -X POST -H "X-Vpn-Token: SECRET" http://192.168.1.55:9777/v1/disconnect
```

## Uninstall

```powershell
.\scripts\uninstall-vpn-agent.ps1 -RemoveData
```

## Notes

- Independent of `Kiberone.Student` / Tutor LAN ports 8765–8767.
- `SERVICE_SID_TYPE_UNRESTRICTED` is set on the tunnel service (required by WireGuard).
- Set `AllowedRemoteAddresses` to the router IP in production.
