# Netplay

This mod allows you to play the game with your friends over the internet.

**Disclaimer:** This mod is experimental and may contain bugs, crashes, or other unexpected issues. Use it at your own risk. It may not always work as intended.

## How it works

Each machine simulates its own player's car and sends that car's state to the host on every simulation tick. The host passes the states on to the other guests. Guests never talk to each other directly, which is why only the host has to be reachable from the internet.

Between updates, the other players' cars keep driving with the last input their owners sent. On a high ping you'll see them snap a little when a new state comes in.

## Requirements

- Everyone needs the same build of the game, with the same version of this mod. The mod copies raw car and race data between machines, so different builds will desync or crash.
- The mod has to be enabled on every machine.
- The host needs a UDP port that guests can reach (7604 unless you change it). See [Making the host reachable](#making-the-host-reachable).
- IPv4. IPv6 addresses don't work.

## Playing

### Hosting

1. Go to **ONLINE > HOST GAME**. The console shows `[netplay] hosting on port 7604`.
2. Tell your guests your address (see below).
3. Pick the track and settings, then your car.
4. Wait for the guests to join and get to their car select. Each one that joins shows up in the console as `joined as player N`.
5. Press **RACE**. It only goes through when at least one guest has joined and every guest is waiting:
   - `NO PLAYERS JOINED`: nobody has connected yet.
   - `PLAYERS READY 1/2`: some guests haven't pressed RACE yet.

### Joining

1. Set `NFS4_NET_ADDR` to the host's address before starting the game (see [Environment variables](#environment-variables)).
2. Go to **ONLINE > JOIN GAME**. This takes you straight to car select. The console shows `joined as player N` once the host answers.
3. Pick your car and press **RACE**. You'll see `WAITING FOR HOST` until the host starts the race.

If the host is already racing, you'll get `RACE IN PROGRESS`. Press RACE again after the race is over.

### During the race

- The countdown starts once every player has loaded the track. If someone hasn't loaded after 60 seconds, the race starts without them.
- Players start in rows of two. Guest 1 is at the front and the host starts last.
- Pausing only pauses your own game. If the host picks Restart or Quit in the pause menu, the race restarts or ends for everybody.
- If a guest quits or drops, their car keeps rolling with the last input they sent.
- After a race everybody is back in the lobby. Guests who stay silent for 5 seconds there are dropped, and the rest are renumbered.
- In the replay after a race, some cars may sit still.

## Environment variables

| Variable | Who | Effect |
|---|---|---|
| `NFS4_NET_ADDR` | guest | address of the host, `host` or `host:port`. Accepts an IPv4 address or a host name. Defaults to `127.0.0.1:7604` |
| `NFS4_NET_ADDR` | host | only the port is used, written as `:port` (for example `:7700`). The host always listens on all network interfaces |
| `NFS4_NET` | both | `host` or `join` starts the session at launch instead of waiting for the ONLINE menu. You still go through the ONLINE menu to get to car select. Whatever you pick in the menu wins |
| `NFS4_NETPLAY_LOG` | both | `1` logs more: every player's car, the race setup differences, car positions once a second |
| `NFS4_EXTRA_HUMANS` | offline | local test without a network. Turns this many AI opponents (0 to 7, default 1) into human cars. The first one is driven by controller 2, the rest sit still |
| `NFS4_NETPLAY_SETUPDUMP` | both | writes the race setup to this file every time a race loads. For debugging |

Setting them:

```powershell
# PowerShell
$env:NFS4_NET_ADDR = "203.0.113.5:7604"
.\nfs4.exe
```

```bat
:: cmd, or save it as join.bat next to nfs4.exe
set NFS4_NET_ADDR=203.0.113.5:7604
nfs4.exe
```

```sh
# Linux
NFS4_NET_ADDR=203.0.113.5:7604 ./nfs4
```

## Making the host reachable

Guests connect to the host on UDP port 7604. Guests don't need to open any ports themselves. The mod has no NAT punch-through and there's no matchmaking server, so the host has to be reachable in one of the ways below.

### Same network (LAN)

Guests use the host's local address, for example `192.168.1.20`. Run `ipconfig` on Windows or `ip addr` on Linux to find it. Only the firewall needs to allow it (below).

### Port forwarding (lowest latency)

1. In the host's router, forward UDP port 7604 (not TCP) to the host PC's local address.
2. Allow it in the host's firewall.
3. Guests use the host's public IP, which you can look up at a site like whatismyip.com.

This doesn't work if the host's ISP uses CGNAT. Use one of the options below in that case.

### Tailscale or ZeroTier (easiest for friends)

Everyone installs [Tailscale](https://tailscale.com) or [ZeroTier](https://www.zerotier.com) once and joins the same network. Guests then use the host's Tailscale/ZeroTier address, for example `100.101.102.103`. There's no router setup. Latency is low when the clients connect directly, and higher when they fall back to a relay.

### playit.gg (guests install nothing)

The host runs the [playit.gg](https://playit.gg) agent and creates a UDP tunnel to local port 7604. playit gives it a public address like `name.playit.gg:12345`, and guests put that in `NFS4_NET_ADDR`. Going through their relay adds some ping.

### Firewall

Windows shows a firewall prompt the first time you host. Allow it for the networks you play on. To add the rule by hand, in an admin PowerShell:

```powershell
New-NetFirewallRule -DisplayName "NFS4 netplay" -Direction Inbound -Protocol UDP -LocalPort 7604 -Action Allow
```

On Linux with ufw:

```sh
sudo ufw allow 7604/udp
```

If you changed the port with `NFS4_NET_ADDR=:port`, use that port everywhere above.

## Troubleshooting

| Problem | What to check |
|---|---|
| No `[netplay]` lines in the console | the mod is disabled, or the game wasn't started from its own folder |
| "could not start the session" when hosting | another program (or a second copy of the game) already uses the port. Close it or pick another port with `NFS4_NET_ADDR=:port` |
| Guest never shows `joined as player N` | wrong address, the host isn't on HOST GAME yet, or the port isn't reachable (firewall, port forward, CGNAT) |
| Guest stuck on `WAITING FOR HOST` | the host hasn't pressed RACE. If it did, check that both are on the same build |
| "got no race setup from the host" | the guest waited 3 minutes without getting the race. The guest then races alone with its own setup |
| "the other players did not reach the race" | someone took longer than 60 seconds to load, or dropped |
| Cars jump around | high ping or packet loss, usually on the host's side. Try a direct connection instead of a relay |
| Desyncs or crashes when the race loads | the players are on different builds or mod versions |

Run with `NFS4_NETPLAY_LOG=1` on every machine and compare the logs when reporting a problem.
