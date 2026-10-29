using System;
using System.Runtime.CompilerServices;
using NeuroCache.ResearchLab.Contracts;

namespace NeuroCache.ResearchLab.Representations;

/// <summary>
/// REP-07: Query-Family Polynomial Code (QFPC)
/// Evaluates query answers via exact polynomial interpolation over a finite domain.
/// Evaluates arithmetic and storage costs vs direct bitwise indexing.
/// </summary>
public sealed class REP07_QueryFamilyPolynomialCode : IExperimentCandidate, IPredicateEvaluator<long>, IMemoryAccountable
{
    public string ExperimentId => "REP-QFPC-007";
    public string CandidateName => "Query-Family Polynomial Code (QFPC)";
    public string Family => "Representations";
    public string Description => "Represents answer vectors algebraically via polynomial coefficients; measures evaluation arithmetic vs bitwise masking.";

    // Polynomial coefficients: P(x) = c0 + c1*x + c2*x^2 + ... + c7*x^7
    // Stored as floating or fixed-point coefficients per record
    private readonly double[][] _coefficients;
    private readonly long _maxId;

    public long MaxId => _maxId;
    public long TotalAllocatedBytes => _coefficients.Length * 64L;
    public long PayloadBytes => TotalAllocatedBytes;
    public long MetadataBytes => 64;

    public REP07_QueryFamilyPolynomialCode(long maxId, Func<long, byte> patternSupplier)
    {
        _maxId = maxId;
        _coefficients = new double[maxId + 1][];

        for (long id = 1; id <= maxId; id++)
        {
            byte pat = patternSupplier(id);
            _coefficients[id] = InterpolatePolynomial(pat);
        }
    }

    /// <summary>
    /// Fits a degree-7 polynomial P(x) such that P(i) = Bit(pat, i) for i in 0..7.
    /// Using Vandermonde matrix inversion for exact interpolation.
    /// </summary>
    private static double[] InterpolatePolynomial(byte pattern)
    {
        var coeffs = new double[8];
        // For demonstration of exact algebraic evaluation:
        // We use Horner's method on the fitted coefficients.
        // For binary outputs on coordinates 0..7, direct Vandermonde solves the exact coefficients.
        double[] y = new double[8];
        for (int i = 0; i < 8; i++)
        {
            y[i] = ((pattern >> (7 - i)) & 1);
        }

        // Vandermonde solver
        coeffs = SolveVandermonde8(y);
        return coeffs;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EvaluatePredicate(long id, int predicateIndex)
    {
        double[] c = _coefficients[id];
        double x = predicateIndex;

        // Horner's evaluation: P(x) = c0 + x*(c1 + x*(c2 + ...))
        double val = c[7];
        for (int i = 6; i >= 0; i--)
        {
            val = val * x + c[i];
        }

        return Math.Round(val) >= 1.0;
    }

    private static double[] SolveVandermonde8(double[] y)
    {
        // 8x8 Vandermonde solver (Gaussian elimination for reference)
        double[,] a = new double[8, 8];
        for (int i = 0; i < 8; i++)
        {
            double p = 1.0;
            for (int j = 0; j < 8; j++)
            {
                a[i, j] = p;
                p *= i;
            }
        }

        double[] b = (double[])y.Clone();
        for (int i = 0; i < 8; i++)
        {
            double pivot = a[i, i];
            for (int j = i; j < 8; j++) a[i, j] /= pivot;
            b[i] /= pivot;

            for (int r = 0; r < 8; r++)
            {
                if (r != i)
                {
                    double factor = a[r, i];
                    for (int j = i; j < 8; j++) a[r, j] -= factor * a[i, j];
                    b[r] -= factor * b[i];
                }
            }
        }
        return b;
    }
}
