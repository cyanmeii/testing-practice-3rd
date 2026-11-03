using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace async_tests.Tests;

/// <summary>
/// Часть 1, задание 1: тесты для PrimesAmount.CountPrimesAmount.
///
/// Особенности метода, которые учтены в тестах:
///  1. Parallel.For выполняет итерации в произвольных потоках и порядке -> порядок элементов
///     ConcurrentQueue НЕ детерминирован, сравнивать нужно как множества (AreEquivalent), а не по индексам.
///  2. Зависимость (PrimeService) вызывается из нескольких потоков -> счётчики вызовов
///     собираются в потокобезопасных коллекциях, а не в обычном List.
///  3. Исключение внутри итерации превращается в AggregateException, которое метод глушит
///     и очищает очередь (даже уже найденные простые) -> результат пуст, причина потеряна.
///  4. Parallel.For(2, border) исключает верхнюю границу.
///  5. finally всегда вызывает ClearCache (кроме border &lt;= 1) -> ClearCache должен вызываться
///     строго ПОСЛЕ завершения всех проверок.
///
/// Тесты с суффиксом _KnownDefect фиксируют ТЕКУЩЕЕ (ошибочное) поведение исходного кода,
/// чтобы дефект был виден и воспроизводим; после исправления их нужно инвертировать.
/// </summary>
[TestClass]
public class PrimesAmountTests
{
    // Независимый «оракул» простоты — не зависит от тестируемого кода.
    private static bool IsPrime(int n)
    {
        if (n < 2) return false;
        for (var i = 2; (long)i * i <= n; i++)
            if (n % i == 0) return false;
        return true;
    }

    private static Mock<PrimeService> ServiceMock(Func<int, bool> isPrime)
    {
        var mock = new Mock<PrimeService>();
        mock.Setup(s => s.CheckIsPrime(It.IsAny<int>())).Returns<int>(n => isPrime(n));
        return mock;
    }

    // ---------- конструктор / свойства ----------

    [TestMethod]
    [DataRow(1)]
    [DataRow(10)]
    [DataRow(int.MaxValue)]
    public void Constructor_PositiveBorder_SetsBorder(int border)
    {
        Assert.AreEqual(border, new PrimesAmount(border).Border);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(int.MinValue)]
    public void Constructor_NonPositiveBorder_ThrowsArgumentException(int border)
    {
        Assert.ThrowsException<ArgumentException>(() => new PrimesAmount(border));
    }

    [TestMethod]
    public void Count_BeforeCalculation_Throws()
    {
        var sut = new PrimesAmount(10);

        var ex = Assert.ThrowsException<Exception>(() => sut.Count);

        Assert.AreEqual("count is not set", ex.Message);
    }

    [TestMethod]
    public void Count_AfterCountPrimesAmount_StillThrows_KnownDefect()
    {
        // Поле count нигде не присваивается, свойство Count недостижимо.
        var sut = new PrimesAmount(10);
        sut.CountPrimesAmount(ServiceMock(IsPrime).Object);

        Assert.ThrowsException<Exception>(() => sut.Count);
    }

    // ---------- граничные значения border ----------

    [TestMethod]
    public void CountPrimesAmount_BorderOne_ReturnsEmpty_AndDoesNotTouchService()
    {
        var service = ServiceMock(_ => true);

        var result = new PrimesAmount(1).CountPrimesAmount(service.Object);

        Assert.AreEqual(0, result.Count);
        service.Verify(s => s.CheckIsPrime(It.IsAny<int>()), Times.Never());
        service.Verify(s => s.ClearCache(), Times.Never());
    }

    [TestMethod]
    public void CountPrimesAmount_BorderTwo_EmptyRange_ReturnsEmpty_ButClearsCache()
    {
        var service = ServiceMock(_ => true);

        var result = new PrimesAmount(2).CountPrimesAmount(service.Object);

        Assert.AreEqual(0, result.Count);
        service.Verify(s => s.CheckIsPrime(It.IsAny<int>()), Times.Never());
        service.Verify(s => s.ClearCache(), Times.Once());
    }

    [TestMethod]
    public void CountPrimesAmount_UpperBoundIsExclusive()
    {
        var service = ServiceMock(_ => true); // всё «простое» -> видно, какие числа проверялись

        var result = new PrimesAmount(5).CountPrimesAmount(service.Object);

        CollectionAssert.AreEquivalent(new[] { 2, 3, 4 }, result.ToArray());
        service.Verify(s => s.CheckIsPrime(5), Times.Never());
    }

    // ---------- асинхронность / параллелизм ----------

    [TestMethod]
    public void CountPrimesAmount_ChecksEveryNumberExactlyOnce_AndReturnsAllPrimesRegardlessOfOrder()
    {
        const int border = 200;
        var calls = new ConcurrentBag<int>(); // потокобезопасно: вызовы приходят из разных потоков
        var service = ServiceMock(n => { calls.Add(n); return IsPrime(n); });

        var result = new PrimesAmount(border).CountPrimesAmount(service.Object);

        CollectionAssert.AreEquivalent(Enumerable.Range(2, border - 2).ToList(), calls.ToList());
        var expected = Enumerable.Range(2, border - 2).Where(IsPrime).ToList();
        CollectionAssert.AreEquivalent(expected, result.ToArray());
        Assert.AreEqual(result.Count, result.Distinct().Count(), "дубликатов быть не должно");
    }

    [TestMethod]
    public void CountPrimesAmount_ClearCacheIsCalledOnceAndOnlyAfterAllChecksFinished()
    {
        const int border = 100;
        var checks = 0;
        var checksSeenByClearCache = -1;
        var service = new Mock<PrimeService>();
        service.Setup(s => s.CheckIsPrime(It.IsAny<int>()))
               .Returns<int>(n => { Interlocked.Increment(ref checks); return IsPrime(n); });
        service.Setup(s => s.ClearCache())
               .Callback(() => checksSeenByClearCache = Volatile.Read(ref checks));

        new PrimesAmount(border).CountPrimesAmount(service.Object);

        service.Verify(s => s.ClearCache(), Times.Once());
        Assert.AreEqual(border - 2, checksSeenByClearCache);
    }

    [TestMethod]
    public void CountPrimesAmount_EachCallReturnsIndependentQueue()
    {
        var service = ServiceMock(IsPrime);
        var sut = new PrimesAmount(20);

        var first = sut.CountPrimesAmount(service.Object);
        var second = sut.CountPrimesAmount(service.Object);

        Assert.AreNotSame(first, second);
        CollectionAssert.AreEquivalent(first.ToArray(), second.ToArray());
    }

    // ---------- ошибки зависимости ----------

    [TestMethod]
    public void CountPrimesAmount_CheckIsPrimeThrowsForOneNumber_ReturnsEmptyAndSwallowsException()
    {
        // Даже уже найденные простые отбрасываются; причина ошибки наружу не попадает.
        var service = ServiceMock(n => n == 50 ? throw new InvalidOperationException("boom") : IsPrime(n));

        var result = new PrimesAmount(100).CountPrimesAmount(service.Object);

        Assert.AreEqual(0, result.Count);
        service.Verify(s => s.ClearCache(), Times.Once());
    }

    [TestMethod]
    public void CountPrimesAmount_EveryCheckThrows_ReturnsEmpty_AndClearsCache()
    {
        var service = ServiceMock(_ => throw new ArgumentException("bad"));

        var result = new PrimesAmount(10).CountPrimesAmount(service.Object);

        Assert.AreEqual(0, result.Count);
        service.Verify(s => s.ClearCache(), Times.Once());
    }

    [TestMethod]
    public void CountPrimesAmount_ClearCacheThrows_ExceptionPropagatesFromFinally()
    {
        var service = ServiceMock(IsPrime);
        service.Setup(s => s.ClearCache()).Throws<InvalidOperationException>();

        Assert.ThrowsException<InvalidOperationException>(() => new PrimesAmount(10).CountPrimesAmount(service.Object));
    }

    [TestMethod]
    public void CountPrimesAmount_NullService_ThrowsNullReferenceException_NoArgumentValidation()
    {
        // Внутри Parallel.For NRE глушится, затем NRE из finally (pService.ClearCache()) вылетает наружу.
        Assert.ThrowsException<NullReferenceException>(() => new PrimesAmount(5).CountPrimesAmount(null!));
    }

    // ---------- реальный ConcretePrimeService ----------

    [TestMethod]
    [DataRow(0)]
    [DataRow(-7)]
    public void ConcretePrimeService_NonPositive_Throws(int p)
    {
        Assert.ThrowsException<ArgumentException>(() => new ConcretePrimeService().CheckIsPrime(p));
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(3, true)]
    [DataRow(4, false)]
    [DataRow(9, false)]
    [DataRow(25, false)]
    [DataRow(49, false)]
    [DataRow(97, true)]
    [DataRow(2147483647, true)]
    public void ConcretePrimeService_CheckIsPrime(int p, bool expected)
    {
        Assert.AreEqual(expected, new ConcretePrimeService().CheckIsPrime(p));
    }

    [TestMethod]
    public void ConcretePrimeService_Two_IsReportedNotPrime_KnownDefect()
    {
        // 2 — простое, но условие p%2!=0 отсекает его. Ожидаемо после исправления: true.
        Assert.IsFalse(new ConcretePrimeService().CheckIsPrime(2));
    }

    [TestMethod]
    public void CountPrimesAmount_WithRealService_MissesTwo_KnownDefect()
    {
        var result = new PrimesAmount(30).CountPrimesAmount(new ConcretePrimeService());

        CollectionAssert.AreEquivalent(new[] { 3, 5, 7, 11, 13, 17, 19, 23, 29 }, result.ToArray());
    }
}
