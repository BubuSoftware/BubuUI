#!/usr/bin/env bash
# Build the Rider ZIP on macOS/Linux without PowerShell or Windows VSIX tooling.
set -euo pipefail

rider_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ide_dir="$(cd -- "$rider_dir/.." && pwd)"
repo_dir="$(cd -- "$ide_dir/.." && pwd)"
skip_tests=false

for argument in "$@"; do
    case "$argument" in
        --skip-tests) skip_tests=true ;;
        --help|-h)
            printf 'Usage: %s [--skip-tests]\n' "$0"
            exit 0
            ;;
        *) printf 'Unknown argument: %s\n' "$argument" >&2; exit 2 ;;
    esac
done

for dependency in dotnet node; do
    if ! command -v "$dependency" >/dev/null 2>&1; then
        printf 'Required command not found: %s\n' "$dependency" >&2
        exit 1
    fi
done

java_command=java
if [[ -n "${JAVA_HOME:-}" ]]; then
    java_command="$JAVA_HOME/bin/java"
fi
if ! java_version_output="$("$java_command" -version 2>&1)"; then
    printf 'Set JAVA_HOME to a JDK 21-24 installation.\n%s\n' "$java_version_output" >&2
    exit 1
fi
java_major="$(printf '%s\n' "$java_version_output" | sed -nE 's/.*version "([0-9]+).*/\1/p' | head -n 1)"
if [[ ! "$java_major" =~ ^(21|22|23|24)$ ]]; then
    printf 'Gradle 9.0 and Rider 2024.2 require JDK 21-24 for this build; detected Java %s. Set JAVA_HOME accordingly.\n' "${java_major:-unknown}" >&2
    exit 1
fi

server_dir="$ide_dir/vscode/server"
dotnet publish "$ide_dir/EclipseUI.LanguageServer" -c Release -o "$server_dir" \
    --nologo --no-self-contained -p:UseAppHost=false

# The metadata catalog uses these assemblies without loading platform code.
dotnet build "$repo_dir/src/EclipseUI.Rendering" -c Release --nologo
for assembly in EclipseUI.Runtime EclipseUI.Rendering; do
    cp "$repo_dir/src/$assembly/bin/Release/net11.0/$assembly.dll" "$server_dir/"
done

if [[ "$skip_tests" == false ]]; then
    dotnet test "$repo_dir/tests/EclipseUI.Ide.Tests" -c Release --nologo
fi

cd -- "$rider_dir"
gradle_tasks=(verifyShell buildPlugin verifyPluginStructure --console=plain)
if [[ "$skip_tests" == false ]]; then
    gradle_tasks=(test "${gradle_tasks[@]}")
fi
./gradlew "${gradle_tasks[@]}"

plugin_version="$(sed -n 's/^pluginVersion=//p' "$rider_dir/gradle.properties")"
printf '\nRider plugin: %s/build/distributions/eclipseui-rider-%s.zip\n' "$rider_dir" "$plugin_version"
