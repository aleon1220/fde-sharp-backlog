# Containers & Podman Kubernetes

Build and run `SmartBacklogAssistant` inside an OCI container using `Containerfile` and `k8s/smart-backlog-pod.yaml`.

## Containers & Podman Kubernetes

```bash
# Deterministic --locked-mode restore & --no-restore publish (optionally pass --build-arg NUGET_PROXY_FEED_URL=...)
podman build -t localhost/smart-backlog-assistant:v1 -f Containerfile .
podman kube play k8s/smart-backlog-pod.yaml
podman logs smart-backlog-assistant-assistant
podman kube down k8s/smart-backlog-pod.yaml
```

See also: [build-from-source.md](../getting-started/build-from-source.md) · [releases.md](releases.md) · [Containerfile](../../Containerfile) · [k8s/smart-backlog-pod.yaml](../../k8s/smart-backlog-pod.yaml)
