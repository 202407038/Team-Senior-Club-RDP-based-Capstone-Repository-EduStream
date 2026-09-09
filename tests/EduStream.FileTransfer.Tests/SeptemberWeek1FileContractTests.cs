using EduStream.Core.Factories;
using EduStream.Core.Models;
using EduStream.Core.Protocols;

namespace EduStream.FileTransfer.Tests;

public sealed class SeptemberWeek1FileContractTests
{
    [Theory]
    [InlineData(ErrorCodes.FileWritePermissionDenied, false)]
    [InlineData(ErrorCodes.FilePathTooLong, false)]
    [InlineData(ErrorCodes.DirectoryNotFound, true)]
    [InlineData(ErrorCodes.FileIoError, true)]
    public void ReceiverStorageFailures_AreValidCommonErrors(string code, bool retryable)
    {
        var info = FeatureErrorCatalog.Resolve(code);
        Assert.Equal(FeatureArea.File, info.Feature);
        var packet = PacketFactory.CreateError("student", code, "storage failed", retryable);
        var status = FeatureOperationResult.FromError(FeatureArea.File, packet);
        Assert.Equal(retryable, status.CanRetry);
        Assert.NotEmpty(status.DisplayMessage);
    }
}
