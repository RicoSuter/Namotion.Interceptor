#!/usr/bin/env bash
# Model-check the OPC UA client lifecycle model.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
model="$here/../../docs/formal/opcua-client"
( cd "$model" && "$here/tlc" OpcUaClient.tla )
