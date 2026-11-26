using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;

namespace BDTool.UnityCore.Tests
{
    /// <summary>
    /// 验证 Prefab 根对象 activeSelf 的读取、dry-run、CAS 与实际写入。
    /// Unity 6 将导入 Prefab 的根对象名呈现为文件 stem；写入验证只依赖 activeSelf，
    /// 不依赖源场景中 GameObject 的原始名称。
    /// </summary>
    [TestFixture]
    public sealed class PrefabActivationServiceTests
    {
        private const string RootObjectName = "Activation";

        private TestAssetFixture _fixture;
        private PrefabActivationService _service;
        private string _assetPath;
        private AssetSnapshot _before;

        [SetUp]
        public void SetUp()
        {
            _fixture = new TestAssetFixture();
            _service = new PrefabActivationService();
            // Unity 6 导入后根对象名与文件 stem 一致；这是真实 API 契约，不是替代写入。
            _assetPath = _fixture.CreatePrefab("Activation.prefab", RootObjectName, 2);
            _before = new AssetSnapshotService().Read(_assetPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (_fixture != null)
            {
                _fixture.Dispose();
            }
        }

        [Test]
        public void SetActive_DryRunPreviewsWithoutChangingFileOrUndo()
        {
            int undoGroupBefore = Undo.GetCurrentGroup();

            PrefabActivationResult result = _service.SetActive(
                _assetPath,
                _before.Sha256,
                false,
                true);

            AssetSnapshot after = new AssetSnapshotService().Read(_assetPath);

            Assert.That(result.DryRun, Is.True);
            Assert.That(result.Changed, Is.True);
            Assert.That(result.Saved, Is.False);
            Assert.That(result.Verified, Is.False);
            Assert.That(result.WasActive, Is.True);
            Assert.That(result.IsActive, Is.False);
            Assert.That(result.PreviousSha256, Is.EqualTo(_before.Sha256));
            Assert.That(result.NewSha256, Is.EqualTo(_before.Sha256));
            Assert.That(result.UndoGroup, Is.Empty);
            Assert.That(after.Sha256, Is.EqualTo(_before.Sha256));
            Assert.That(TestAssetFixture.ReadRootActiveSelf(_assetPath), Is.True);
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroupBefore));
        }

        [Test]
        public void SetActive_WritesInactiveStateAndVerifiesReadBack()
        {
            PrefabActivationResult result = _service.SetActive(
                _assetPath,
                _before.Sha256,
                false,
                false);

            AssetSnapshot after = new AssetSnapshotService().Read(_assetPath);
            string diskSha256 = TestAssetFixture.ComputeFileSha256(_assetPath);
            int undoGroup;
            bool undoGroupParsed = int.TryParse(result.UndoGroup, out undoGroup);

            Assert.That(result.DryRun, Is.False);
            Assert.That(result.Changed, Is.True);
            Assert.That(result.Saved, Is.True);
            Assert.That(result.Verified, Is.True);
            Assert.That(result.WasActive, Is.True);
            Assert.That(result.IsActive, Is.False);
            Assert.That(result.PreviousSha256, Is.EqualTo(_before.Sha256));
            Assert.That(result.NewSha256, Is.Not.EqualTo(_before.Sha256));
            Assert.That(result.NewSha256, Is.EqualTo(diskSha256));
            Assert.That(undoGroupParsed, Is.True);
            Assert.That(undoGroup, Is.GreaterThanOrEqualTo(0));
            Assert.That(TestAssetFixture.ReadRootActiveSelf(_assetPath), Is.False);
            Assert.That(after.Sha256, Is.EqualTo(result.NewSha256));
            Assert.That(after.RootObjectName, Is.EqualTo(RootObjectName));
            Assert.That(after.AssetPath, Is.EqualTo(_assetPath));
            Assert.That(after.ChildCount, Is.EqualTo(_before.ChildCount));
        }

        [Test]
        public void SetActive_ActivatesInactiveRootAndVerifiesReadBack()
        {
            string inactiveAssetPath = _fixture.CreatePrefab("Inactive.prefab", "InactiveRoot", 1, rootActive: false);
            AssetSnapshot before = new AssetSnapshotService().Read(inactiveAssetPath);

            PrefabActivationResult result = _service.SetActive(
                inactiveAssetPath,
                before.Sha256,
                true,
                false);

            Assert.That(result.WasActive, Is.False);
            Assert.That(result.IsActive, Is.True);
            Assert.That(result.Changed, Is.True);
            Assert.That(result.Saved, Is.True);
            Assert.That(result.Verified, Is.True);
            Assert.That(result.NewSha256, Is.Not.EqualTo(before.Sha256));
            Assert.That(TestAssetFixture.ReadRootActiveSelf(inactiveAssetPath), Is.True);
        }

        [Test]
        public void SetActive_StaleCasThrowsBeforeUndoOrFileChanges()
        {
            string wrongSha256 = new string('0', 64);
            if (string.Equals(wrongSha256, _before.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                wrongSha256 = new string('1', 64);
            }

            int undoGroupBefore = Undo.GetCurrentGroup();

            UnityCoreException exception = Assert.Throws<UnityCoreException>(() => _service.SetActive(
                _assetPath,
                wrongSha256,
                false,
                false));

            AssetSnapshot after = new AssetSnapshotService().Read(_assetPath);

            Assert.That(exception.Code, Is.EqualTo(UnityCoreErrorCode.PreconditionFailed));
            Assert.That(after.Sha256, Is.EqualTo(_before.Sha256));
            Assert.That(TestAssetFixture.ReadRootActiveSelf(_assetPath), Is.True);
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroupBefore));
        }

        [Test]
        public void SetActive_SameStateDoesNotSaveOrCreateUndoGroup()
        {
            int undoGroupBefore = Undo.GetCurrentGroup();

            PrefabActivationResult result = _service.SetActive(
                _assetPath,
                _before.Sha256,
                true,
                false);

            AssetSnapshot after = new AssetSnapshotService().Read(_assetPath);

            Assert.That(result.Changed, Is.False);
            Assert.That(result.Saved, Is.False);
            Assert.That(result.Verified, Is.False);
            Assert.That(result.WasActive, Is.True);
            Assert.That(result.IsActive, Is.True);
            Assert.That(result.NewSha256, Is.EqualTo(_before.Sha256));
            Assert.That(result.UndoGroup, Is.Empty);
            Assert.That(after.Sha256, Is.EqualTo(_before.Sha256));
            Assert.That(TestAssetFixture.ReadRootActiveSelf(_assetPath), Is.True);
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroupBefore));
        }

        [Test]
        public void SetActive_ExpectedShaComparisonIsCaseInsensitive()
        {
            PrefabActivationResult result = _service.SetActive(
                _assetPath,
                _before.Sha256.ToUpperInvariant(),
                false,
                true);

            Assert.That(result.Changed, Is.True);
            Assert.That(result.Saved, Is.False);
            Assert.That(result.PreviousSha256, Is.EqualTo(_before.Sha256));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void SetActive_RequiresExpectedSha256(string expectedSha256)
        {
            AssertRejectedWithoutSideEffects(expectedSha256, false);
        }

        [TestCase("Assets/../ProjectSettings/Evil.prefab")]
        [TestCase("../Assets/Evil.prefab")]
        [TestCase("Packages/com.example/Evil.prefab")]
        [TestCase("AssetsX/Evil.prefab")]
        public void SetActive_RejectsPathEscapeWithoutSideEffects(string assetPath)
        {
            int undoGroupBefore = Undo.GetCurrentGroup();

            UnityCoreException exception = Assert.Throws<UnityCoreException>(() => _service.SetActive(
                assetPath,
                _before.Sha256,
                false,
                false));

            AssetSnapshot after = new AssetSnapshotService().Read(_assetPath);

            Assert.That(exception.Code, Is.EqualTo(UnityCoreErrorCode.InvalidArgument));
            Assert.That(after.Sha256, Is.EqualTo(_before.Sha256));
            Assert.That(TestAssetFixture.ReadRootActiveSelf(_assetPath), Is.True);
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroupBefore));
        }

        [Test]
        public void SetActive_RejectsNonPrefabAsset()
        {
            string assetPath = _fixture.AssetFolderPath + "/NotAPrefab.txt";
            int undoGroupBefore = Undo.GetCurrentGroup();

            UnityCoreException exception = Assert.Throws<UnityCoreException>(() => _service.SetActive(
                assetPath,
                _before.Sha256,
                false,
                false));

            Assert.That(exception.Code, Is.EqualTo(UnityCoreErrorCode.NotPrefab));
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroupBefore));
        }

        [Test]
        public void SetActive_MissingPrefabThrowsAssetNotFoundWithoutSideEffects()
        {
            string missingPath = _fixture.AssetFolderPath + "/Missing.prefab";
            int undoGroupBefore = Undo.GetCurrentGroup();

            UnityCoreException exception = Assert.Throws<UnityCoreException>(() => _service.SetActive(
                missingPath,
                _before.Sha256,
                false,
                false));

            Assert.That(exception.Code, Is.EqualTo(UnityCoreErrorCode.AssetNotFound));
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroupBefore));
        }

        [Test]
        public void SetActive_DoesNotChangeAssetPathOrFileStem()
        {
            string fileStem = Path.GetFileNameWithoutExtension(_assetPath);

            Assert.That(fileStem, Is.EqualTo("Activation"));
            Assert.That(_before.RootObjectName, Is.EqualTo(RootObjectName));
            Assert.That(fileStem, Is.EqualTo(_before.RootObjectName));

            PrefabActivationResult result = _service.SetActive(
                _assetPath,
                _before.Sha256,
                false,
                false);

            AssetSnapshot after = new AssetSnapshotService().Read(_assetPath);

            Assert.That(result.Saved, Is.True);
            Assert.That(result.Verified, Is.True);
            Assert.That(after.RootObjectName, Is.EqualTo(RootObjectName));
            Assert.That(after.AssetPath, Is.EqualTo(_assetPath));
            Assert.That(Path.GetFileNameWithoutExtension(after.AssetPath), Is.EqualTo(fileStem));
        }

        private void AssertRejectedWithoutSideEffects(string expectedSha256, bool isActive)
        {
            int undoGroupBefore = Undo.GetCurrentGroup();

            UnityCoreException exception = Assert.Throws<UnityCoreException>(() => _service.SetActive(
                _assetPath,
                expectedSha256,
                isActive,
                false));

            AssetSnapshot after = new AssetSnapshotService().Read(_assetPath);

            Assert.That(exception.Code, Is.EqualTo(UnityCoreErrorCode.InvalidArgument));
            Assert.That(after.Sha256, Is.EqualTo(_before.Sha256));
            Assert.That(TestAssetFixture.ReadRootActiveSelf(_assetPath), Is.True);
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroupBefore));
        }
    }
}
