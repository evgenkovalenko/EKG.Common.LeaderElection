# EKG.Common.LeaderElection

Redis-backed leader election library for EKG services. Runs as a .NET hosted background service — one instance in a cluster holds the lock at any time.

## How it works

Each application instance generates a unique `InstanceId` (GUID) on startup. Every 5 seconds the background loop either:

- **Renews** the lock if the current Redis key already belongs to this instance (extends TTL).
- **Tries to acquire** using `SET NX` (set only if the key does not exist). Only one instance wins the race.

The Redis key stores a JSON value:

```json
{
  "holderId": "...",
  "transitionCount": 3,
  "renewTime": "2025-01-01T12:00:05Z",
  "acquireTime": "2025-01-01T12:00:00Z"
}
```

| Field | Description |
|---|---|
| `holderId` | GUID of the current leader instance |
| `transitionCount` | Incremented each time a new instance becomes leader |
| `renewTime` | Updated on every loop iteration by the current leader |
| `acquireTime` | Set when this `holderId` first acquired leadership |

On graceful shutdown the leader deletes the key immediately, allowing the next election to happen before the TTL expires.

## Redis key format

```
leader-election:{AppName}
```

Example: `leader-election:EKG.App.Jpe.BE`

## Packages

```xml
<PackageReference Include="EKG.Common.LeaderElection" Version="1.0.*" />
```

Requires `EKG.Common.Cache.Redis` to register `IConnectionMultiplexer` first.

## Configuration

```json
{
  "Redis": {
    "ConnectionString": "redis://localhost:6379"
  },
  "LeaderElection": {
    "TtlSeconds": 15,
    "AppName": "EKG.App.Jpe.BE"
  }
}
```

| Key | Default | Description |
|---|---|---|
| `LeaderElection:TtlSeconds` | `15` | Redis key TTL in seconds |
| `LeaderElection:AppName` | _(required)_ | Included in the Redis key to scope per application |

## Registration

```csharp
builder.Services.AddRedisCache(builder.Configuration);     // registers IConnectionMultiplexer
builder.Services.AddLeaderElection(builder.Configuration); // registers leader election
```

## Usage

```csharp
public class MyService(ILeaderElectionService leaderElection)
{
    public void DoWork()
    {
        if (!leaderElection.IsLeader)
            return; // only the leader does the work

        // ...
    }
}
```

## Running tests

**Unit tests** (no external dependencies):
```bash
dotnet test EKG.Common.LeaderElection.Tests.Unit
```

**E2E tests** (requires Docker / Rancher Desktop):
```bash
dotnet test EKG.Common.LeaderElection.Tests.E2E
```

The E2E fixture starts `docker compose up --build -d` automatically (Redis + 2× Sample app instances) and tears down on completion.

## Publishing

Pushing any commit to `main` triggers the GitHub Actions workflow, which builds and publishes the `EKG.Common.LeaderElection` package to GitHub Packages. Version scheme: `{major}.{minor}.{run_number}`.
