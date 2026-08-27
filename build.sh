#!/bin/bash
# Forwarder — canonical build script lives at repo root
exec "$(cd "$(dirname "$0")/.." && pwd)"/build.sh "$@"
