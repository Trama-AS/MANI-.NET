using ManiDispatch.Application.Interfaces;
using ManiDispatch.Application.UseCases;
using ManiDispatch.Domain.Entities;
using Moq;
using Xunit;

namespace ManiDispatch.Tests;

/// <summary>
/// SCRUM-1652: las lecturas que no dependen entre sí van a la vez. Cada doble espera a que la otra lectura haya empezado;
/// si el caso de uso volviera a pedirlas una tras otra, la espera vence y la prueba falla en vez de colgarse.
/// </summary>
public class GetEligibleAlliesConcurrencyTests
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _requestId = Guid.NewGuid();
    private readonly Guid _usuarioId = Guid.NewGuid();
    private readonly Mock<IAllyRepository> _repo = new();
    private readonly Mock<ISolicitudAssignmentRepository> _assignments = new();
    private readonly Mock<ITenantRulesReader> _tenantRules = new();
    private readonly Mock<IRulesClient> _rules = new();

    private GetEligibleAlliesUseCase NewUseCase() =>
        new(_repo.Object, _assignments.Object, _tenantRules.Object, _rules.Object);

    private SolicitudContext Contexto() => new()
    {
        Id = _requestId,
        TenantId = _tenantId,
        ZonaId = Guid.NewGuid(),
        CategoriaId = Guid.NewGuid(),
        Estado = "PENDIENTE"
    };

    private void RulesKeepsTheOrderItReceives() =>
        _rules.Setup(r => r.RankAlliesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Ally>>(), _tenantId, "cliente", "corr"))
              .ReturnsAsync((string _, IReadOnlyCollection<Ally> c, Guid _, string _, string _) =>
                  c.Select((a, i) => new RankedAlly(a.Id, i + 1, null, Array.Empty<string>())).ToList());

    [Fact]
    public async Task ContextAndRejections_AreRequestedAtTheSameTime()
    {
        var rechazosPedidos = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contextoPedido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rechazado = Guid.NewGuid();

        _repo.Setup(r => r.GetSolicitudContextAsync(_requestId, _tenantId)).Returns(async () =>
        {
            contextoPedido.TrySetResult();
            await rechazosPedidos.Task.WaitAsync(Espera);
            return Contexto();
        });
        _assignments.Setup(a => a.GetRejectedAllyIdsAsync(_requestId, _tenantId, _usuarioId, "cliente")).Returns(async () =>
        {
            rechazosPedidos.TrySetResult();
            await contextoPedido.Task.WaitAsync(Espera);
            return (IReadOnlyList<Guid>)new[] { rechazado };
        });
        _repo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>()))
             .ReturnsAsync((Enumerable.Empty<Ally>(), 0));
        _tenantRules.Setup(t => t.GetCriterioOrdenListadoAsync(_tenantId, "corr")).ReturnsAsync("CALIFICACION");

        await NewUseCase().ExecuteForRequestAsync(_requestId, _tenantId, _usuarioId, "cliente", null, null, null, "corr");

        // Los rechazos siguen llegando a Core como exclusión de la lista.
        _repo.Verify(r => r.GetEligibleAlliesAsync(It.Is<MatchCriteria>(c => c.ExcludeAllyIds.Contains(rechazado))), Times.Once);
    }

    [Fact]
    public async Task TenantCriterion_IsRequestedWhileTheEligibleListIsBeingRead()
    {
        var criterioPedido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listaPedida = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aliado = new Ally { Id = Guid.NewGuid(), NombreRazonSocial = "Aliado" };

        _repo.Setup(r => r.GetSolicitudContextAsync(_requestId, _tenantId)).ReturnsAsync(Contexto());
        _assignments.Setup(a => a.GetRejectedAllyIdsAsync(_requestId, _tenantId, _usuarioId, "cliente"))
                    .ReturnsAsync(Array.Empty<Guid>());
        _repo.Setup(r => r.GetEligibleAlliesAsync(It.IsAny<MatchCriteria>())).Returns(async () =>
        {
            listaPedida.TrySetResult();
            await criterioPedido.Task.WaitAsync(Espera);
            return (new[] { aliado }.AsEnumerable(), 1);
        });
        _tenantRules.Setup(t => t.GetCriterioOrdenListadoAsync(_tenantId, "corr")).Returns(async () =>
        {
            criterioPedido.TrySetResult();
            await listaPedida.Task.WaitAsync(Espera);
            return "CALIFICACION";
        });
        RulesKeepsTheOrderItReceives();

        var result = await NewUseCase().ExecuteForRequestAsync(_requestId, _tenantId, _usuarioId, "cliente", null, null, null, "corr");

        Assert.Equal("CALIFICACION", result.Ranking!.Criterio);
        Assert.Equal(aliado.Id.ToString(), result.MatchedAllies.Single().AllyId);
    }

    [Fact]
    public async Task WhenTheContextDoesNotExist_ItIsStillA404_EvenIfRejectionsFail()
    {
        _repo.Setup(r => r.GetSolicitudContextAsync(_requestId, _tenantId)).ReturnsAsync((SolicitudContext?)null);
        _assignments.Setup(a => a.GetRejectedAllyIdsAsync(_requestId, _tenantId, _usuarioId, "cliente"))
                    .ThrowsAsync(new InvalidOperationException("base caída"));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            NewUseCase().ExecuteForRequestAsync(_requestId, _tenantId, _usuarioId, "cliente", null, null, null, "corr"));
    }
}
