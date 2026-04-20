using EKG.Common.LeaderElection.Tests.E2E.Infrastructure;

namespace EKG.Common.LeaderElection.Tests.E2E;

[CollectionDefinition("DockerCompose")]
public class DockerComposeCollection : ICollectionFixture<DockerComposeFixture>
{
}
