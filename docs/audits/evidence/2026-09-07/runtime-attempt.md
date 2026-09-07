# Promptly runtime setup report

Date: 2026-09-07 (America/Chicago)

## Scope

Attempted to start the unmodified Promptly `main` baseline for local browser QA. Existing Docker data, repository files, and commits were preserved. No application source or Git-tracked file was changed.

## Initial state

The existing Colima profile was stopped. The Docker client selected context `colima`, but that context was missing its metadata file:

```text
colima status
time="2026-09-07T16:28:19-05:00" level=fatal msg="colima is not running"

docker context ls
colima * ... ERROR context "colima" not found: open /Users/jake/.docker/contexts/meta/.../meta.json: no such file or directory
```

No Promptly containers were listed before startup. The Docker volume listing contained two pre-existing anonymous volume IDs; they were not modified.

## Colima startup

The existing profile was started with:

```text
colima start --cpu 4 --memory 8 --disk 30
```

Startup completed successfully. Colima reported the existing `colima` instance was used, the Docker context was successfully created, and Docker became ready. At report time Colima remains running.

Observed runtime configuration:

```text
arch: aarch64
runtime: docker
mountType: virtiofs
docker socket: unix:///Users/jake/.colima/default/docker.sock
```

## Isolated Compose setup

Created only this temporary file:

`/private/tmp/promptly-audit-20260907/runtime/compose.override.yml`

The override gives the Postgres, API, worker, and optional web services unique audit container/volume names. It maps the API to `localhost:5000`, the worker to `localhost:8000`, and profiles out the web service because the frontend was intended to run separately on `localhost:3000`. The worker was configured with provider `none` and empty credentials to avoid paid LLM calls.

Compose configuration validation succeeded:

```text
docker compose -p promptly-audit-20260907 \
  -f docker/docker-compose.yml \
  -f /private/tmp/promptly-audit-20260907/runtime/compose.override.yml \
  config --services
```

The command listed `promptly-eval`, `postgres`, and `promptly-server` for the selected profile.

## Build failure

The only runtime build attempt was:

```text
docker compose -p promptly-audit-20260907 \
  -f docker/docker-compose.yml \
  -f /private/tmp/promptly-audit-20260907/runtime/compose.override.yml \
  up -d --build postgres promptly-eval promptly-server
```

It failed while building the Python worker image, before any Promptly containers were created:

```text
Docker Compose requires buildx plugin to be installed
Step 1/8 : FROM python:3.11-slim
 ---> 90744cff8f32
Step 2/8 : WORKDIR /app
commit failed: write /var/lib/containerd/io.containerd.snapshotter.v1.overlayfs/metadata.db: input/output error
```

After the failure, `docker compose ... ps -a` returned no containers for the audit project.

## Storage and filesystem evidence

Host filesystem space was critically low:

```text
df -h /
/dev/disk3s1s1  228Gi  17Gi  288Mi  99%  ... /
/dev/disk3s5     228Gi 187Gi  288Mi 100%  ... /System/Volumes/Data
```

Colima guest space was available but its Docker data disk reported filesystem errors:

```text
/dev/root  19G  1.3G  18G   7% /
/dev/vdb1  30G   21G  7.3G 74% /mnt/lima-colima
```

`colima ssh -- sudo dmesg | tail -80` reported:

```text
EXT4-fs (vdb1): warning: mounting fs with errors, running e2fsck is recommended
I/O error, dev vdb, sector ... op 0x1:(WRITE)
EXT4-fs warning (device vdb1): ext4_end_bio: ... I/O error ...
EXT4-fs (vdb1): failed to convert unwritten extents to written extents -- potential data loss!
EXT4-fs (vdb1): Aborting journal on device vdb1-8.
JBD2: I/O error when updating journal superblock for vdb1-8.
EXT4-fs error (device vdb1): ... Detected aborted journal
```

Docker image metadata inspection also failed with a missing/unreadable content blob:

```text
docker image ls
rpc error: code = Unknown desc = blob sha256:... expected at
/var/lib/containerd/io.containerd.content.v1.content/blobs/sha256/...:
open ...: input/output error
```

`docker system df -v` failed with the same class of blob read error.

## Endpoint verification

Because the build failed before container creation, these endpoints were unavailable:

```text
http://localhost:5000/health  -> curl HTTP code 000 / connection refused
http://localhost:8000/health  -> curl HTTP code 000 / connection refused
http://localhost:3000        -> curl HTTP code 000 / connection refused
```

No API, worker, or browser QA assertions were run.

## Disposition

No further Docker attempts were made. The practical prerequisite is to reclaim host disk space and then repair or replace the Colima Docker data disk after preserving any required Docker data. This report does not authorize deletion, filesystem repair, Colima reset, or Docker data cleanup.
