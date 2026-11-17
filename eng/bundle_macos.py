#!/usr/bin/env python3
"""Publish and package EclipseUI desktop applications without machine-local dylib links."""
import argparse
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile

MACHO_MAGIC = {bytes.fromhex(value) for value in
               ("feedface", "cefaedfe", "feedfacf", "cffaedfe", "cafebabe", "bebafeca", "cafebabf", "bfbafeca")}


def run(*args, capture=False):
    result = subprocess.run([str(arg) for arg in args], check=True,
                            stdout=subprocess.PIPE if capture else None, text=True)
    return result.stdout.strip() if capture else ""


def is_macho(path):
    if not path.is_file() or path.is_symlink():
        return False
    with path.open("rb") as stream:
        return stream.read(4) in MACHO_MAGIC


def is_system(path):
    return path.startswith(("/System/Library/", "/usr/lib/"))


def dependencies(path, arch):
    output = run("otool", "-arch", arch, "-L", path, capture=True)
    return re.findall(r"^\s+(.+?) \(compatibility version", output, re.MULTILINE)


def install_id(path, arch):
    lines = run("otool", "-arch", arch, "-D", path, capture=True).splitlines()
    return lines[-1] if len(lines) > 1 else None


def rpaths(path, arch):
    output = run("otool", "-arch", arch, "-l", path, capture=True)
    return re.findall(r"cmd LC_RPATH\s+cmdsize \d+\s+path (.+?) \(offset", output)


def signing_order(graph):
    """Dependencies precede dependents; dylib cycles are signed once before the app."""
    ordered, visited = [], set()

    def visit(binary):
        if binary in visited:
            return
        visited.add(binary)
        for child in sorted(graph.get(binary, ())):
            visit(child)
        ordered.append(binary)

    for binary in sorted(graph):
        visit(binary)
    return ordered


class Bundle:
    def __init__(self, app, arch, library_dirs=()):
        self.app = Path(app).resolve()
        self.macos = self.app / "Contents/MacOS"
        self.payload = self.app / "Contents/Resources/app"
        self.frameworks = self.app / "Contents/Frameworks"
        self.arch = arch
        self.library_dirs = [Path(path).expanduser().resolve() for path in library_dirs]
        self.sources = {}
        self.graph = {}

    def architectures(self, path):
        return run("lipo", "-archs", path, capture=True).split()

    def check_architecture(self, path):
        arches = self.architectures(path)
        if self.arch not in arches:
            raise ValueError(f"{path} has {'/'.join(arches)}, expected {self.arch}; provide matching libraries with --library-dir")
        return arches

    def prepare_binary(self, path):
        arches = self.check_architecture(path)
        # Mutate only files already copied into the staging bundle.
        subprocess.run(["codesign", "--remove-signature", str(path)],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        if len(arches) > 1:
            thin = path.with_name(path.name + ".thin")
            run("lipo", path, "-thin", self.arch, "-output", thin)
            thin.replace(path)

    def resolve(self, dependency, source):
        # Explicit directories override the build machine's install names, useful for cross publishing.
        candidates = [directory / Path(dependency).name for directory in self.library_dirs]
        if dependency.startswith("@loader_path/"):
            candidates.append(source.parent / dependency[len("@loader_path/"):])
        elif dependency.startswith("@executable_path/"):
            candidates.append(self.payload / dependency[len("@executable_path/"):])
        elif dependency.startswith("@rpath/"):
            suffix = dependency[len("@rpath/"):]
            for entry in rpaths(source, self.arch):
                entry = entry.replace("@loader_path", str(source.parent)).replace("@executable_path", str(self.payload))
                candidates.append(Path(entry) / suffix)
        else:
            candidates.append(Path(dependency))
        candidates.extend([source.parent / Path(dependency).name,
                           self.payload / Path(dependency).name, self.frameworks / Path(dependency).name])
        for candidate in candidates:
            if candidate.is_file():
                self.check_architecture(candidate)
                return candidate.resolve()
        raise ValueError(f"Cannot resolve {dependency} required by {source}; install its target-architecture runtime or use --library-dir")

    def copy_dependency(self, source):
        if source.is_relative_to(self.app):
            return source
        if ".framework" in str(source):
            raise ValueError(f"Third-party framework dependency {source} needs a dylib distribution for this packager")
        destination = self.frameworks / source.name
        previous = self.sources.get(destination)
        if previous is not None and previous != source:
            raise ValueError(f"Conflicting dependency name {source.name}: {previous} and {source}")
        if previous is None:
            self.sources[destination] = source
            shutil.copy2(source, destination)
        return destination

    def relocate(self, binary, source=None):
        if binary in self.graph:
            return
        source = source or binary
        self.graph[binary] = []
        self.prepare_binary(binary)
        own_id = install_id(source, self.arch)
        links = dependencies(source, self.arch)
        if own_id:
            run("install_name_tool", "-id", "@rpath/" + binary.name, binary)
        for dependency in links:
            if dependency == own_id or is_system(dependency):
                continue
            resolved = self.resolve(dependency, source)
            target = self.copy_dependency(resolved)
            self.graph[binary].append(target)
            relative = os.path.relpath(target, binary.parent)
            run("install_name_tool", "-change", dependency, "@loader_path/" + relative, binary)
            self.relocate(target, self.sources.get(target))

    def bundle(self, sdl_path=None):
        self.frameworks.mkdir(parents=True, exist_ok=True)
        local_sdl = self.payload / "libSDL3.dylib"
        sdl = (Path(sdl_path).expanduser().resolve() if sdl_path else
               local_sdl.resolve() if local_sdl.is_file() else self.resolve("libSDL3.0.dylib", self.payload / "app"))
        self.check_architecture(sdl)
        sdl_target = self.copy_dependency(sdl)
        self.relocate(sdl_target, sdl)
        for binary in sorted(self.app.rglob("*")):
            if is_macho(binary):
                self.relocate(binary)
        # SDL's managed resolver intentionally checks an app-local name before system locations.
        sdl_alias = self.payload / "libSDL3.dylib"
        if sdl_alias == sdl_target:
            return self.graph
        if sdl_alias.exists() and not sdl_alias.is_symlink():
            raise ValueError(f"Publish directory already contains {sdl_alias.name}; omit --sdl-library to use that library")
        if sdl_alias.is_symlink():
            sdl_alias.unlink()
        sdl_alias.symlink_to(os.path.relpath(sdl_target, self.payload))
        return self.graph

    def verify(self):
        binaries = [path for path in self.app.rglob("*") if is_macho(path)]
        if not binaries:
            raise ValueError("Bundle contains no Mach-O executable")
        manifest = []
        for binary in sorted(binaries):
            self.check_architecture(binary)
            own_id = install_id(binary, self.arch)
            links = dependencies(binary, self.arch)
            for dependency in links:
                if dependency == own_id or is_system(dependency):
                    continue
                if not dependency.startswith("@loader_path/"):
                    raise ValueError(f"Nonportable dependency {dependency} in {binary}")
                target = (binary.parent / dependency[len("@loader_path/"):]).resolve()
                if not target.is_relative_to(self.app) or not target.is_file():
                    raise ValueError(f"Dependency escapes or is missing from bundle: {dependency} in {binary}")
            manifest.append({"path": str(binary.relative_to(self.app)), "architecture": self.arch, "dependencies": links})
        for link in self.app.rglob("*"):
            if link.is_symlink() and (not link.resolve().is_relative_to(self.app) or not link.exists()):
                raise ValueError(f"Bundle symlink escapes or is broken: {link}")
        return manifest


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--project", default="samples/Gallery.Desktop/Gallery.Desktop.csproj")
    result.add_argument("--publish-dir", type=Path, help="Use an existing self-contained publish output")
    result.add_argument("--output", type=Path, default=Path("out/macos/Gallery.app"))
    result.add_argument("--name", default="EclipseUI Gallery")
    result.add_argument("--executable", default="Gallery.Desktop")
    result.add_argument("--bundle-id", default="io.eclipseui.gallery")
    result.add_argument("--version", default="1.0.0")
    result.add_argument("--configuration", default="Release")
    result.add_argument("--rid", choices=("osx-arm64", "osx-x64"), default="osx-arm64" if os.uname().machine == "arm64" else "osx-x64")
    result.add_argument("--library-dir", action="append", default=[], help="Search directory for target-architecture external dylibs; repeatable")
    result.add_argument("--sdl-library", type=Path)
    result.add_argument("--sign-identity", default="-", help="codesign identity; '-' creates the default local ad hoc signature")
    result.add_argument("--notary-profile", help="Opt in to uploading with an existing notarytool keychain profile; requires Developer ID signing")
    result.add_argument("--minimum-system-version", default="13.0")
    result.add_argument("--force", action="store_true", help="Replace an existing .app only after the new bundle validates")
    result.add_argument("--verify", type=Path, help="Verify an existing bundle and exit")
    return result


def main():
    options = parser().parse_args()
    if sys.platform != "darwin":
        raise ValueError("macOS packaging requires macOS with Xcode Command Line Tools")
    for command in ("otool", "lipo", "install_name_tool", "codesign", "clang"):
        if not shutil.which(command):
            raise ValueError(f"Missing {command}; install Xcode Command Line Tools")
    arch = "arm64" if options.rid == "osx-arm64" else "x86_64"
    if options.verify:
        bundle = Bundle(options.verify, arch)
        print(json.dumps(bundle.verify(), indent=2))
        run("codesign", "--verify", "--deep", "--strict", options.verify)
        return
    if options.notary_profile and options.sign_identity == "-":
        raise ValueError("--notary-profile requires --sign-identity with a Developer ID Application certificate")
    output = options.output.expanduser().absolute()
    if output.suffix != ".app":
        raise ValueError("--output must end in .app")
    if output.exists() and not options.force:
        raise ValueError(f"Output already exists: {output}; use --force to replace it")
    if Path(options.executable).name != options.executable:
        raise ValueError("--executable must be a file name, not a path")
    if options.publish_dir and output.parent.resolve().is_relative_to(options.publish_dir.resolve()):
        raise ValueError("--output must be outside --publish-dir to avoid recursively copying the staging bundle")
    output.parent.mkdir(parents=True, exist_ok=True)
    repo = Path(__file__).resolve().parent.parent
    library_dirs = options.library_dir + ["/opt/homebrew/lib", "/usr/local/lib"]
    with tempfile.TemporaryDirectory(prefix=".eclipseui-package-", dir=output.parent) as temporary:
        temporary = Path(temporary)
        publish = options.publish_dir.resolve() if options.publish_dir else temporary / "publish"
        if not options.publish_dir:
            project = Path(options.project)
            if not project.is_absolute():
                project = repo / project
            run("dotnet", "publish", project, "-c", options.configuration, "-r", options.rid,
                "--self-contained", "true", "-o", publish)
        executable = publish / options.executable
        if not executable.is_file():
            raise ValueError(f"Published executable is missing: {executable}")
        app = temporary / output.name
        macos = app / "Contents/MacOS"
        resources = app / "Contents/Resources"
        payload = resources / "app"
        shutil.copytree(publish, payload, symlinks=True)
        macos.mkdir(parents=True)
        # Keep managed assemblies in Resources: codesign treats files in MacOS as nested code.
        # A native launcher preserves argv and execs the signed .NET apphost in the payload.
        run("clang", "-arch", arch, "-mmacosx-version-min=" + options.minimum_system_version,
            "-Os", repo / "eng/macos-launcher.c", "-o", macos / options.executable)
        with (app / "Contents/Info.plist").open("wb") as stream:
            plistlib.dump({"CFBundleExecutable": options.executable, "CFBundleIdentifier": options.bundle_id,
                          "CFBundleName": options.name, "CFBundleDisplayName": options.name,
                          "CFBundleVersion": options.version, "CFBundleShortVersionString": options.version,
                          "CFBundlePackageType": "APPL", "NSHighResolutionCapable": True,
                          "NSPrincipalClass": "NSApplication", "LSMinimumSystemVersion": options.minimum_system_version}, stream)
        bundle = Bundle(app, arch, library_dirs)
        graph = bundle.bundle(options.sdl_library)
        manifest = bundle.verify()
        (resources / "native-dependencies.json").write_text(json.dumps(manifest, indent=2) + "\n")
        entitlements = temporary / "entitlements.plist"
        with entitlements.open("wb") as stream:
            plistlib.dump({"com.apple.security.cs.allow-jit": True,
                          "com.apple.security.cs.disable-library-validation": True}, stream)
        timestamp = "--timestamp=none" if options.sign_identity == "-" else "--timestamp"
        for binary in signing_order(graph):
            args = ["codesign", "--force", "--sign", options.sign_identity, "--options", "runtime", timestamp]
            if binary in ((macos / options.executable).resolve(), (payload / options.executable).resolve()):
                args.extend(["--entitlements", str(entitlements)])
            run(*args, binary)
        run("codesign", "--force", "--sign", options.sign_identity, "--options", "runtime", timestamp,
            "--entitlements", entitlements, app)
        run("codesign", "--verify", "--deep", "--strict", app)
        if options.notary_profile:
            archive = temporary / "notarize.zip"
            run("ditto", "-c", "-k", "--keepParent", app, archive)
            run("xcrun", "notarytool", "submit", archive, "--keychain-profile", options.notary_profile, "--wait")
            run("xcrun", "stapler", "staple", app)
            run("xcrun", "stapler", "validate", app)
        if output.exists():
            shutil.rmtree(output)
        shutil.move(app, output)
    print(f"Packaged {output} ({arch}, {'ad hoc' if options.sign_identity == '-' else 'Developer ID'} signature)")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print(f"Packaging failed: {error}", file=sys.stderr)
        sys.exit(1)
