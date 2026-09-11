using System;
using System.Threading.Tasks;
using Maple2.Server.Global.Service;

namespace Maple2.Server.Tests.World;

public class AccountLoginTests {
    [TestCase("", "password123")]
    [TestCase("player", "")]
    [TestCase("player", "    ")]
    public async Task EmptyCredentialsAreRejectedInEveryBuildConfiguration(string username, string password) {
        using var service = new GlobalService(null!);
        LoginResponse response = await service.Login(new LoginRequest {
            Username = username,
            Password = password,
            MachineId = Guid.NewGuid().ToString(),
            ClientIp = "127.0.0.1",
        }, null!);
        Assert.That(response.Code, Is.Not.EqualTo(LoginResponse.Types.Code.Ok));
        Assert.That(response.AccountId, Is.Zero);
    }

    [Test]
    public async Task InvalidClientIdentityIsRejectedBeforeDatabaseAccess() {
        using var service = new GlobalService(null!);
        LoginResponse response = await service.Login(new LoginRequest {
            Username = "player",
            Password = "password123",
            MachineId = "not-a-guid",
            ClientIp = "127.0.0.1",
        }, null!);
        Assert.That(response.Code, Is.EqualTo(LoginResponse.Types.Code.SessionError));
    }

    [Test]
    public async Task LoginAttemptsShareABoundedPerAddressLimit() {
        using var service = new GlobalService(null!);
        var request = new LoginRequest { ClientIp = "127.0.0.1" };
        for (int i = 0; i < 20; i++) {
            await service.Login(request, null!);
        }
        LoginResponse rejected = await service.Login(request, null!);
        Assert.That(rejected.Message, Does.Contain("Too many login attempts"));
        request.ClientIp = "127.0.0.2";
        Assert.That((await service.Login(request, null!)).Message, Does.Not.Contain("Too many"));
    }
}
