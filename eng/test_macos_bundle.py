#!/usr/bin/env python3
"""Portable dependency checks plus a real macOS bundle/relocation/signing fixture."""
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("bundle_macos", Path(__file__).with_name("bundle_macos.py"))
packaging = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packaging)


class DependencyTests(unittest.TestCase):
    def test_signs_dependencies_before_dependents(self):
        self.assertEqual(packaging.signing_order({"app": ["middle"], "middle": ["base"], "base": []}),
                         ["base", "middle", "app"])

    def test_signs_cycles_only_once(self):
        order = packaging.signing_order({"app": ["a"], "a": ["b"], "b": ["a"]})
        self.assertEqual(len(order), 3)
        self.assertEqual(order[-1], "app")

    def test_rejects_wrong_architecture(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = packaging.Bundle(directory, "x86_64")
            with patch.object(bundle, "architectures", return_value=["arm64"]):
                with self.assertRaisesRegex(ValueError, "expected x86_64"):
                    bundle.check_architecture(Path(directory) / "native.dylib")

    def verify_fixture(self, dependency=None, symlink=None):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory) / "Fixture.app"
            native = app / "Contents/MacOS/Fixture"
            native.parent.mkdir(parents=True)
            native.write_bytes(bytes.fromhex("cffaedfe"))
            if symlink:
                (app / "link").symlink_to(symlink)
            bundle = packaging.Bundle(app, "arm64")
            with patch.object(bundle, "architectures", return_value=["arm64"]), \
                 patch.object(packaging, "install_id", return_value=None), \
                 patch.object(packaging, "dependencies", return_value=[dependency] if dependency else []):
                return bundle.verify()

    def test_rejects_absolute_dependency(self):
        with self.assertRaisesRegex(ValueError, "Nonportable dependency"):
            self.verify_fixture("/opt/homebrew/lib/libexample.dylib")

    def test_rejects_unresolved_rpath(self):
        with self.assertRaisesRegex(ValueError, "Nonportable dependency"):
            self.verify_fixture("@rpath/libexample.dylib")

    def test_rejects_missing_relative_dependency(self):
        with self.assertRaisesRegex(ValueError, "escapes or is missing"):
            self.verify_fixture("@loader_path/../Frameworks/missing.dylib")

    def test_rejects_escaping_relative_dependency(self):
        with self.assertRaisesRegex(ValueError, "escapes or is missing"):
            self.verify_fixture("@loader_path/../../../../outside.dylib")

    def test_rejects_outside_symlink(self):
        with self.assertRaisesRegex(ValueError, "symlink escapes or is broken"):
            self.verify_fixture(symlink="/usr/lib")

    def test_rejects_broken_symlink(self):
        with self.assertRaisesRegex(ValueError, "symlink escapes or is broken"):
            self.verify_fixture(symlink="missing")

    def test_accepts_system_dependencies(self):
        self.assertEqual(len(self.verify_fixture("/usr/lib/libSystem.B.dylib")), 1)


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("clang"), "macOS/Xcode Command Line Tools required")
class NativeBundleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory()
        cls.root = Path(cls.temporary.name)
        cls.external = cls.root / "external"
        cls.external.mkdir()
        cls.publish = cls.root / "publish"
        cls.publish.mkdir()
        cls.app = cls.root / "Fixture.app"
        cls.arch = "arm64" if os.uname().machine == "arm64" else "x86_64"
        cls.rid = "osx-arm64" if cls.arch == "arm64" else "osx-x64"
        for name, source in (("base", "int base(void) { return 7; }"),
                             ("middle", "extern int base(void); int middle(void) { return 35 + base(); }"),
                             ("SDL3.0", "int SDL_Init(void) { return 1; }")):
            c = cls.root / (name + ".c")
            c.write_text(source)
            library = cls.external / ("lib" + name + ".dylib")
            args = ["clang", "-dynamiclib", "-arch", cls.arch, "-Wl,-headerpad_max_install_names",
                    str(c), "-install_name", str(library), "-o", str(library)]
            if name == "middle":
                args.extend(["-L" + str(cls.external), "-lbase"])
            subprocess.run(args, check=True, capture_output=True)
        main = cls.root / "main.c"
        main.write_text('extern int middle(void); int main(void) { return middle() == 42 ? 0 : 1; }')
        subprocess.run(["clang", "-arch", cls.arch, "-Wl,-headerpad_max_install_names", str(main),
                        "-L" + str(cls.external), "-lmiddle", "-o", str(cls.publish / "Fixture")],
                       check=True, capture_output=True)
        # A managed assembly must be stored as an ordinary resource, not nested MacOS code.
        (cls.publish / "Managed.dll").write_bytes(b"MZ-managed-fixture")
        cls.command = [sys.executable, str(Path(__file__).with_name("bundle_macos.py")),
                       "--publish-dir", str(cls.publish), "--output", str(cls.app),
                       "--executable", "Fixture", "--rid", cls.rid,
                       "--sdl-library", str(cls.external / "libSDL3.0.dylib"),
                       "--library-dir", str(cls.external)]
        result = subprocess.run(cls.command, text=True, capture_output=True)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        cls.external.rename(cls.root / "unavailable-original-libraries")

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def test_runs_after_original_libraries_are_removed(self):
        environment = {key: value for key, value in os.environ.items() if not key.startswith("DYLD_")}
        subprocess.run([str(self.app / "Contents/MacOS/Fixture")], check=True, env=environment)

    def test_has_standard_layout_and_valid_signature(self):
        self.assertTrue((self.app / "Contents/Resources/app/Managed.dll").is_file())
        self.assertFalse((self.app / "Contents/MacOS/Managed.dll").exists())
        subprocess.run(["codesign", "--verify", "--deep", "--strict", str(self.app)], check=True)
        manifest = packaging.Bundle(self.app, self.arch).verify()
        self.assertTrue(any(entry["path"].endswith("libbase.dylib") for entry in manifest))

    def test_existing_bundle_is_preserved_after_failed_replacement(self):
        result = subprocess.run(self.command + ["--force"], capture_output=True)
        self.assertNotEqual(result.returncode, 0)  # Original source libraries no longer exist.
        subprocess.run([str(self.app / "Contents/MacOS/Fixture")], check=True)

    def test_existing_bundle_requires_force(self):
        result = subprocess.run(self.command, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Output already exists", result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
