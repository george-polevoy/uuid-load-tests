using Dodo.Primitives;

namespace AdHocTests;

public class BrokenUuidTests
{
    [Explicit]
    [Test]
    public void TestBrokenUuids()
    {
        for (var i = 0; i < 20; i++)
        {
            var id = HackedUuid.NewMySqlOptimized(DateTime.Now.AddMinutes(i));
            TestContext.WriteLine($"{Convert.ToHexString(UuidToBytes(id))} {Convert.ToHexString(UuidToBrokenBytes(id))}");
        }
    }

    public static byte[] UuidToBrokenBytes(Uuid id)
    {
        var bytes = new byte[16];
        var broken = new Guid(id.ToString());
        broken.TryWriteBytes(bytes);
        return bytes;
    }

    public static byte[] UuidToBytes(Uuid id)
    {
        var bytes = new byte[16];
        id.TryWriteBytes(bytes);
        return bytes;
    }


}

public static class HackedUuid
{
    private const long ChristianCalendarGregorianReformTicksDate = 499_163_040_000_000_000L;

    private const byte ResetVersionMask = 0b0000_1111;
    private const byte Version1Flag = 0b0001_0000;

    private const byte ResetReservedMask = 0b0011_1111;
    private const byte ReservedFlag = 0b1000_0000;

    public static unsafe Uuid NewMySqlOptimized(DateTime utcNow)
    {
        byte* resultPtr = stackalloc byte[16];
        var resultAsGuidPtr = (Guid*)resultPtr;
        var guid = Guid.NewGuid();
        resultAsGuidPtr[0] = guid;
        long currentTicks = utcNow.Ticks - ChristianCalendarGregorianReformTicksDate;
        var ticksPtr = (byte*)&currentTicks;
        resultPtr[0] = (byte)((ticksPtr[7] & ResetVersionMask) | Version1Flag);
        resultPtr[1] = ticksPtr[6];
        resultPtr[2] = ticksPtr[5];
        resultPtr[3] = ticksPtr[4];
        resultPtr[4] = ticksPtr[3];
        resultPtr[5] = ticksPtr[2];
        resultPtr[6] = ticksPtr[1];
        resultPtr[7] = ticksPtr[0];
        resultPtr[8] = (byte)((resultPtr[8] & ResetReservedMask) | ReservedFlag);
        return new Uuid(resultPtr);
    }
}