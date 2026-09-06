using System;
using System.Collections.Generic;
using System.IO;
using Maple2.File.Ingest.Mapper;
using Maple2.Model.Metadata;

namespace Maple2.Server.Tests.File.Ingest;

public class ConstantsMapperTests {
    [Test]
    public void MergesRequiredClientValuesAndServerOverrides() {
        var client = new List<(string Key, string Value)> {
            ("characterMaxLevel", "60"),
            ("MailExpiryDays", "7"),
        };
        foreach (var property in typeof(ConstantsTable).GetProperties()) {
            if (property.Name.StartsWith("bagSlotTab", StringComparison.Ordinal) &&
                property.PropertyType == typeof(short[])) {
                client.Add((property.Name, "24,48"));
            }
        }

        ConstantsTable constants = ServerTableMapper.BuildConstants(client, [
            ("MailExpiryDays", "30"),
            ("GlobalCubeSkillIntervalTime", "0.1"),
            ("UgcHomeSaleWaitingTime", "60"),
        ]);

        Assert.That(constants.characterMaxLevel, Is.EqualTo(60));
        Assert.That(constants.bagSlotTabPetEquipCount, Is.EqualTo(new short[] { 24, 48 }));
        Assert.That(constants.MailExpiryDays, Is.EqualTo(30));
        Assert.That(constants.GlobalCubeSkillIntervalTime, Is.EqualTo(TimeSpan.FromMilliseconds(100)));
    }

    [Test]
    public void ServerOnlyInputCannotHideMissingClientConstants() {
        Assert.Throws<InvalidDataException>(() => ServerTableMapper.BuildConstants([], [
            ("MailExpiryDays", "30"),
            ("GlobalCubeSkillIntervalTime", "0.1"),
            ("UgcHomeSaleWaitingTime", "60"),
        ]));
    }
}
