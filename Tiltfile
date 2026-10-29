# The E2E bench for EKG.Common.LeaderElection (PRM-78): the sample, two replicas, on a local kind cluster.
#
# Usage: kind create cluster --name leader-election    (once)
#        tilt up --context kind-leader-election
#        dotnet test EKG.Common.LeaderElection.Tests.E2E  (LE_E2E_CONTEXT defaults to kind-leader-election)
#        tilt down
#
# Only kind contexts are accepted: this bench creates Leases and deletes pods, so it must never reach a shared cluster.

if not k8s_context().startswith('kind-'):
    fail("EKG.Common.LeaderElection E2E runs on kind only; current context is '%s'. Use: tilt up --context kind-<cluster>" % k8s_context())

allow_k8s_contexts(k8s_context())

docker_build('le-sample', '.', dockerfile='Dockerfile',
             only=['EKG.Common.LeaderElection', 'EKG.Common.LeaderElection.Sample'],
             ignore=['**/bin', '**/obj'])

k8s_yaml('k8s/sample.yaml')

k8s_resource('le-sample', objects=[
    'leader-election-e2e:namespace',
    'le-sample:serviceaccount',
    'le-sample-leases:role',
    'le-sample-leases:rolebinding',
])
