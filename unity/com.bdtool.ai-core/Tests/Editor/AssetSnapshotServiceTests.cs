using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BDTool.UnityCore.Tests
{
    [TestFixture]
    public sealed class AssetSnapshotServiceTests
    {
        private TestAssetFixture _fixture;
        private AssetSnapshotService _service;

        [SetUp]
        public void SetUp()
        {
            _fixture = new TestAssetFixture();
            _service = new AssetSnapshotService();
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
        public void Read_ExistingPrefab_ReturnsRawHashAndRootFacts()
        {
            string assetPath = _fixture.CreatePrefab("Snapshot.prefab", "SnapshotRoot", 2);
            string expectedSha256 = TestAssetFixture.ComputeFileSha256(assetPath);

            AssetSnapshot snapshot = _service.Read(assetPath);

            Assert.That(snapshot.Exists, Is.True);
            Assert.That(snapshot.AssetPath, Is.EqualTo(assetPath));
            Assert.That(snapshot.Sha256, Is.EqualTo(expectedSha256));
            // Unity 6 导入 Prefab 后，资产根对象名呈现为文件 stem。
            Assert.That(snapshot.RootObjectName, Is.EqualTo("Snapshot"));
            Assert.That(snapshot.ChildCount, Is.EqualTo(2));
            Assert.That(snapshot.UnityVersion, Is.EqualTo(Application.unityVersion));
        }

        [Test]
        public void Read_MissingPrefab_ReturnsExistsFalse()
        {
            string missingPath = _fixture.AssetFolderPath + "/Missing.prefab";

            AssetSnapshot snapshot = _service.Read(missingPath);

            Assert.That(snapshot.Exists, Is.False);
            Assert.That(snapshot.AssetPath, Is.EqualTo(missingPath));
            Assert.That(snapshot.Sha256, Is.Empty);
            Assert.That(snapshot.RootObjectName, Is.Empty);
            Assert.That(snapshot.ChildCount, Is.Zero);
        }

        [TestCase("Assets/../ProjectSettings/Evil.prefab")]
        [TestCase("../Assets/Evil.prefab")]
        [TestCase("Packages/com.example/Evil.prefab")]
        [TestCase("AssetsX/Evil.prefab")]
        [TestCase("/Assets/Evil.prefab")]
        public void Read_RejectsUnsafeOrOutOfScopePaths(string assetPath)
        {
            UnityCoreException exception = Assert.Throws<UnityCoreException>(() => _service.Read(assetPath));

            Assert.That(exception.Code, Is.EqualTo(UnityCoreErrorCode.InvalidArgument));
        }

        [Test]
        public void Read_RejectsAbsolutePath()
        {
            string absolutePath = Path.Combine(Path.GetTempPath(), "Evil.prefab");

            UnityCoreException exception = Assert.Throws<UnityCoreException>(() => _service.Read(absolutePath));

            Assert.That(exception.Code, Is.EqualTo(UnityCoreErrorCode.InvalidArgument));
        }

        [Test]
        public void Read_RejectsNonPrefabAsset()
        {
            string assetPath = _fixture.AssetFolderPath + "/NotAPrefab.txt";

            UnityCoreException exception = Assert.Throws<UnityCoreException>(() => _service.Read(assetPath));

            Assert.That(exception.Code, Is.EqualTo(UnityCoreErrorCode.NotPrefab));
        }
    }
}
