#!/bin/sh
# Builds the water simulation library into bin/ (gitignored: run after a fresh checkout).
# Run again after changing water.cpp. macOS: universal (arm64 + x86_64). Linux/Windows lines are for later.
set -e
cd "$(dirname "$0")"
mkdir -p bin
case "$(uname -s)" in
  Darwin)
    clang++ -O3 -ffp-contract=off -std=c++20 -shared -fPIC -fvisibility=hidden -arch arm64 -arch x86_64 \
      -mmacosx-version-min=11.0 -o bin/libcitysim_water.dylib water.cpp ;;
  Linux)
    g++ -O3 -ffp-contract=off -std=c++20 -shared -fPIC -fvisibility=hidden -pthread -o bin/libcitysim_water.so water.cpp ;;
  *)
    echo "Windows: cl /O2 /std:c++20 /LD water.cpp /Fe:bin\\citysim_water.dll" >&2; exit 1 ;;
esac
echo "Built $(ls bin/*citysim_water*)"
