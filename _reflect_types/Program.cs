using DatReaderWriter.Enums;

foreach (var n in Enum.GetNames(typeof(StipplingType)))
    Console.WriteLine($"StipplingType.{n}");
