using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Maple2.File.Flat;
using Maple2.File.Flat.maplestory2library;
using Maple2.File.Ingest.Mapper;
using Maple2.Model.Metadata;

namespace Maple2.Server.Tests.File.Ingest;

public class MapEntityMapperTests {
    [TestCase("02000328_bf", "18300003", 18300003)] // Toxic Garden
    [TestCase("52000120_qd", "18100052", 18100052)] // Henesys bomb
    public void TriggerCubeObjectWeaponEmitsBothRoles(string xblock, string itemCode, int expectedItemId) {
        const string entityId = "0fd52acf-daa4-4033-94d8-652a6e3db65b";
        IMS2TriggerCube cube = InterfaceProxy<IMS2TriggerCube>.Create(new Dictionary<string, object?> {
            [nameof(IMapEntity.EntityId)] = entityId,
            [nameof(IMapEntity.EntityName)] = "trigger-object-weapon",
            [nameof(IMS2TriggerCube.IsVisible)] = true,
            [nameof(IMS2MapProperties.IsObjectWeapon)] = true,
            [nameof(IMS2PhysXProp.ObjectWeaponItemCode)] = itemCode,
            [nameof(IMS2PhysXProp.Position)] = new Vector3(1, 2, 3),
            [nameof(IMS2PhysXProp.Rotation)] = new Vector3(4, 5, 6),
        });

        MapEntity[] result = MapEntityMapper.ParseMap(xblock, [cube]).ToArray();

        Assert.Multiple(() => {
            Assert.That(result, Has.Length.EqualTo(2));
            Assert.That(result.Count(entity => entity.Block is Ms2TriggerCube), Is.EqualTo(1));
            Assert.That(result.Count(entity => entity.Block is ObjectWeapon), Is.EqualTo(1));
            Assert.That(result.Select(entity => entity.Guid), Is.Unique);
            Assert.That(((ObjectWeapon) result.Single(entity => entity.Block is ObjectWeapon).Block).ItemIds,
                Is.EqualTo(new[] { expectedItemId }));
        });
    }

    [Test]
    public void RelatedGuidIgnoresSourceGuidSpelling() {
        Guid compact = MapEntityMapper.CreateRelatedGuid("0FD52ACFDAA4403394D8652A6E3DB65B", "object-weapon");
        Guid formatted = MapEntityMapper.CreateRelatedGuid("0fd52acf-daa4-4033-94d8-652a6e3db65b", "object-weapon");

        Assert.That(formatted, Is.EqualTo(compact));
    }

    public class InterfaceProxy<T> : DispatchProxy where T : class {
        private IReadOnlyDictionary<string, object?> values = null!;

        public static T Create(IReadOnlyDictionary<string, object?> values) {
            T proxy = DispatchProxy.Create<T, InterfaceProxy<T>>();
            ((InterfaceProxy<T>) (object) proxy).values = values;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            string propertyName = targetMethod?.Name.Replace("get_", "") ?? "";
            if (values.TryGetValue(propertyName, out object? value)) {
                return value;
            }
            return targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
