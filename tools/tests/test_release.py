import importlib.util
import hashlib
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from zipfile import ZipFile

spec = importlib.util.spec_from_file_location('release_tools', Path(__file__).parents[1] / 'release_tools.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name, text in {
            'theme/theme.yaml': 'Version: 1.5.6\n', 'SeleneCore/extension.yaml': 'Version: 1.5.6\n',
            'CHANGELOG.md': '# Changes\n## Unreleased\n\n- A fix with C:\\media.\n\n## 1.5.6 - 2026-11-05\n',
            **{f'manifests/{n}.yaml': f'Packages:\n  - Version: 1.5.6\n    PackageUrl: https://example.test/v1.5.6/{n}.zip\n' for n in ('Selene', 'SeleneCore')}
        }.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding='utf-8')

    def snapshot(self):
        return {str(p.relative_to(self.root)): p.read_bytes() for p in self.root.rglob('*') if p.is_file()}

    def test_plan_does_not_write(self):
        before = self.snapshot()
        changes = release.plan(self.root, '1.5.7')
        self.assertEqual(before, self.snapshot())
        self.assertEqual(len(changes), 5)
        self.assertIn('C:\\media.', changes['manifests/Selene.yaml'])

    def test_missing_marker_causes_no_partial_changes(self):
        (self.root / 'manifests/SeleneCore.yaml').write_text('  - Version: 1.5.6\nhttps://example.test/v1.5.6/SeleneCore.zip\n')
        before = self.snapshot()
        with self.assertRaises(ValueError): release.plan(self.root, '1.5.7')
        self.assertEqual(before, self.snapshot())

    def test_rejects_old_and_invalid_versions(self):
        for target in ('1.5.6', '1.5.5', 'v1.5.7', 'bad'):
            with self.assertRaises(ValueError): release.plan(self.root, target)

    def test_versions_must_match(self):
        (self.root / 'SeleneCore/extension.yaml').write_text('Version: 1.5.5\n')
        with self.assertRaises(ValueError): release.plan(self.root, '1.5.7')

    def test_empty_notes_rejected(self):
        (self.root / 'CHANGELOG.md').write_text('## Unreleased\n\n## 1.5.6 - today\n')
        with self.assertRaises(ValueError): release.plan(self.root, '1.5.7')

    def test_write_plan_updates_all_metadata(self):
        release.write_plan(self.root, release.plan(self.root, '1.5.7'))
        self.assertEqual(release.metadata(self.root), '1.5.7')

    def test_failed_replace_rolls_back(self):
        before = self.snapshot()
        original = Path.replace
        calls = 0
        def fail_second(path, target):
            nonlocal calls
            calls += 1
            if calls == 2: raise OSError('Simulated write failure')
            return original(path, target)
        with patch.object(Path, 'replace', fail_second):
            with self.assertRaises(OSError): release.write_plan(self.root, release.plan(self.root, '1.5.7'))
        self.assertEqual(before, self.snapshot())

    def artifacts(self, package_version='1.5.6'):
        dist = self.root / 'dist'
        dist.mkdir(exist_ok=True)
        sums = []
        for name, entry in (('Selene.pthm', 'theme.yaml'), ('SeleneCore.pext', 'extension.yaml')):
            with ZipFile(dist / name, 'w') as archive:
                archive.writestr(entry, f'Version: {package_version}\n')
            sums.append(hashlib.sha256((dist / name).read_bytes()).hexdigest() + '  ' + name)
        (dist / 'SHA256SUMS').write_text('\n'.join(sums))
        (dist / 'BUILDINFO.txt').write_text('Source commit: ' + 'a' * 40 + '\nLocal test mode: False\n')

    def test_matching_packages(self):
        self.artifacts()
        with patch.object(release, 'git', return_value=''):
            self.assertEqual(release.packages(self.root), '1.5.6')

    def test_tampered_package(self):
        self.artifacts()
        with (self.root / 'dist/Selene.pthm').open('ab') as file: file.write(b'tampered')
        with patch.object(release, 'git', return_value=''):
            with self.assertRaisesRegex(ValueError, 'Checksum'): release.packages(self.root)

    def test_wrong_package_version(self):
        self.artifacts('1.5.5')
        with patch.object(release, 'git', return_value=''):
            with self.assertRaisesRegex(ValueError, 'version mismatch'): release.packages(self.root)

    def test_stale_code(self):
        self.artifacts()
        with patch.object(release, 'git', return_value='SeleneCore/changed.cs'):
            with self.assertRaisesRegex(ValueError, 'older than'): release.packages(self.root)

    def test_cli_refuses_dirty_tree(self):
        with patch.object(release, 'ROOT', self.root), patch.object(release, 'git', return_value=' M changed'), patch('sys.argv', ['release.py', '1.5.7', '--dry-run']):
            with self.assertRaises(SystemExit) as error: release.main()
            self.assertEqual(error.exception.code, 1)

    def test_cli_refuses_main(self):
        with patch.object(release, 'ROOT', self.root), patch.object(release, 'git', side_effect=['', 'main']), patch('sys.argv', ['release.py', '1.5.7', '--dry-run']):
            with self.assertRaises(SystemExit) as error: release.main()
            self.assertEqual(error.exception.code, 1)
