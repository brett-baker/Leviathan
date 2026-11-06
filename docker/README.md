# Network test lab

Samba and Toxiproxy for Leviathan's network tests (spec 19.2). Test values only; never point Leviathan at real shares from here.

```
docker compose -f docker/compose.yml up -d     # start
docker compose -f docker/compose.yml down -v   # stop and forget the share
```

| What | Where |
|---|---|
| Samba (SMB2), share `leviathan`, user `lab` | `localhost:1445` |
| Toxiproxy API | `localhost:8474` |
| Proxy in front of Samba (created by the tests) | `localhost:2445` |

Tests in `tests/Leviathan.Integration.Tests/Lab` use it when it is running and skip with the reason when it is not.
The chaos suite (`tests/Leviathan.Chaos.Tests`) maps the share through the proxy with `net use /TCPPORT` and cuts the line mid-copy; it removes its mapping afterwards.
`tests/Leviathan.Integration.Tests/Lab/NetworkLab.cs` adds latency, bandwidth caps and a cut line for the chaos suite (G2).
