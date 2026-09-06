using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Maple2.File.Ingest.Utils;
using Maple2.Model.Metadata;

namespace Maple2.Server.Tests.File.Ingest;

public class GenericHelperTests {
    private enum TestValue {
        None,
        Enabled,
    }

    private sealed class Values {
        public int Number { get; set; }
        public float Ratio { get; set; }
        public bool Flag { get; set; }
        public TestValue Choice { get; set; }
        public short[] Entries { get; set; } = [];
        public Vector3 Position { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    [Test]
    public void SetValue_ConvertsSupportedMetadataValues() {
        var values = new Values();
        var constants = new ConstantsTable();

        Set(values, nameof(Values.Number), "012");
        Set(values, nameof(Values.Ratio), "1.25f");
        Set(values, nameof(Values.Flag), "1");
        Set(values, nameof(Values.Choice), "enabled");
        Set(values, nameof(Values.Entries), "1, 2, 3");
        Set(values, nameof(Values.Position), "1.5, 2.5, 3.5");
        Set(values, nameof(Values.Duration), "01:02:03");
        Set(values, nameof(Values.Timestamp), "2026-09-06T11:45:30Z");
        Set(constants, nameof(ConstantsTable.MeratAirTaxiPrice), "25");

        Assert.Multiple(() => {
            Assert.That(values.Number, Is.EqualTo(12));
            Assert.That(values.Ratio, Is.EqualTo(1.25f));
            Assert.That(values.Flag, Is.True);
            Assert.That(values.Choice, Is.EqualTo(TestValue.Enabled));
            Assert.That(values.Entries, Is.EqualTo(new short[] { 1, 2, 3 }));
            Assert.That(values.Position, Is.EqualTo(new Vector3(1.5f, 2.5f, 3.5f)));
            Assert.That(values.Duration, Is.EqualTo(new TimeSpan(1, 2, 3)));
            Assert.That(values.Timestamp, Is.EqualTo(DateTime.Parse("2026-09-06T11:45:30Z",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind)));
            Assert.That(constants.MeratAirTaxiPrice, Is.EqualTo(25));
        });
    }

    [Test]
    public void SetValue_InvalidValueThrows() {
        var values = new Values();

        Assert.Throws<FormatException>(() => Set(values, nameof(Values.Number), "not-a-number"));
        Assert.Throws<FormatException>(() => Set(values, nameof(Values.Position), "1,2"));
    }

    [Test]
    public void ConstantsTable_RejectsMissingRequiredMetadata() {
        Assert.Throws<InvalidDataException>(() => new ConstantsTable().Validate());
    }

    [Test]
    public void ConstantsTable_RoundTripsAsServerTable() {
        ServerTable value = new ConstantsTable(MeratAirTaxiPrice: 25);

        string json = JsonSerializer.Serialize(value);
        ServerTable? result = JsonSerializer.Deserialize<ServerTable>(json);

        Assert.That(result, Is.TypeOf<ConstantsTable>());
        Assert.That(((ConstantsTable) result!).MeratAirTaxiPrice, Is.EqualTo(25));
    }

    private static void Set(object target, string propertyName, string input) {
        GenericHelper.SetValue(target.GetType().GetProperty(propertyName)!, target, input);
    }
}
