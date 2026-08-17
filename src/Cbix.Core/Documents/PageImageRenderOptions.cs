namespace Cbix.Core.Documents;

/// <summary>
/// How finely a document's pages are rasterised for the generic-vision profile, and the resource
/// ceilings that rasterisation is not allowed to exceed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two of these three settings are attacker-facing security bounds, not operator conveniences.</b>
/// The party that supplies a document controls its declared page geometry and its page count, and
/// both drive allocation directly. <see cref="Dpi"/> only multiplies what the document asks for -
/// it is the one setting the deployment owns - so bounding DPI alone bounds nothing.
/// </para>
/// <para>
/// <b>Measured amplification, which is why the ceilings exist.</b> A hand-built <b>434-byte</b> PDF
/// declaring one page with a 14400 x 14400 point <c>/MediaBox</c> (200 x 200 inches - a legal PDF,
/// well inside the format's 14400-point maximum) rasterises at the <em>default</em> DPI to
/// 30000 x 30000 pixels: <b>900 megapixels, 3.35 GB</b> at 4 bytes per pixel. That is roughly eight
/// million times amplification from a file that sails through every byte-length check ingest has.
/// Separately, a <b>1.05 MB</b> PDF declares <b>5000 pages</b>, and page count multiplies whatever
/// per-page cost survives. Neither is caught by <c>DocumentIngestOptions.MaxDocumentBytes</c>,
/// because neither is large as a file.
/// </para>
/// <para>
/// <b>The ceilings are checked in plain code before any rasterisation happens</b>, per the
/// governing principle: geometry is read from the document's own page table, multiplied out against
/// the configured DPI, and compared against a bound - and a document that exceeds it is refused
/// rather than rendered smaller. Refusing rather than downscaling is deliberate: PDFtoImage's
/// <c>RenderOptions.Width</c>/<c>Height</c> could cap the output absolutely, but that would
/// silently shrink a matrix page until its cells were unreadable while the run still reported full
/// visual fidelity - the exact outcome the capability flag exists to prevent. A refusal routes to
/// review; a silent downscale produces confident wrong data.
/// </para>
/// <para>
/// Members are get-only on purpose: the compiler-generated copy constructor behind a <c>with</c>
/// expression bypasses this constructor's validation, so <c>init</c> setters would open a route
/// around every bound below.
/// </para>
/// </remarks>
public sealed record PageImageRenderOptions
{
    /// <summary>
    /// The default rasterisation density, in dots per inch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The driver is matrix legibility.</b> Sprint 02's Matrix agent has to read 8-10pt type out
    /// of table cells; 150 DPI is the long-standing floor at which text that size survives
    /// rasterisation and OCR-grade reading. An A4 page (595.28 x 841.89 points, measured from the DE
    /// specimen) renders at 150 DPI to 1240 x 1754 pixels.
    /// </para>
    /// <para>
    /// <b>Be precise about what the model actually receives, because the arithmetic cuts the other
    /// way.</b> Mainstream vision APIs downscale any image whose long edge exceeds roughly 1568
    /// pixels, so a 1754-pixel-tall render is <em>not</em> delivered at 150 DPI - it arrives scaled
    /// to about 1568 pixels, an effective density near 134 DPI. Rendering at 150 does not preserve
    /// 150. What it does is ensure the downscale is a downscale rather than an upscale: the
    /// resampler is given more information than it needs and discards some, which is the right side
    /// of that operation to be on, and it leaves headroom for providers whose threshold is higher.
    /// </para>
    /// <para>
    /// <b>Why not higher.</b> Above this the extra pixels are discarded on arrival while still being
    /// paid for in render time, memory and upload bytes; 300 DPI quadruples the pixel count to
    /// produce the same image after downscaling.
    /// </para>
    /// <para>
    /// <b>Why it is a setting and not a constant.</b> A starting point, not dogma - the same stance
    /// design 5.4 takes on model tiering. If the Sprint 02 eval harness shows matrix cell accuracy
    /// short under this profile, DPI is the first thing to move, and the harness reports per-profile
    /// metrics precisely so that decision is made on measurements.
    /// </para>
    /// </remarks>
    public const int DefaultDpi = 150;

    /// <summary>
    /// The lowest density accepted. Below roughly this, body text in a table cell stops being
    /// reliably legible, and a profile that reports full visual content while sending pages the
    /// model cannot read would be worse than the text-only profile, which at least declares itself
    /// degraded.
    /// </summary>
    public const int MinimumDpi = 72;

    /// <summary>
    /// The highest density accepted. A bound on operator error rather than on an attacker - the
    /// per-page ceiling below is what bounds a hostile document - but the two multiply, so this
    /// keeps the product of a mistyped DPI and a legitimate page inside the same envelope.
    /// </summary>
    public const int MaximumDpi = 600;

    /// <summary>
    /// The default ceiling on how many pages one document may have before it is refused unrendered.
    /// </summary>
    /// <remarks>
    /// Country manuals run to a handful of pages (design 5.1) and the Claude Files API's own PDF
    /// ceiling is 600 pages, so 500 refuses nothing this pipeline is built for while bounding a
    /// cost that is otherwise linear in a number the document supplies. It is doing real work:
    /// a 1.05 MB file declaring 5000 pages was measured, and page count multiplies every per-page
    /// cost behind it.
    /// </remarks>
    public const int DefaultMaxPageCount = 500;

    /// <summary>Upper bound on <see cref="MaxPageCount"/> itself, so the ceiling cannot be configured into irrelevance by a typo.</summary>
    public const int MaximumMaxPageCount = 10_000;

    /// <summary>
    /// The default ceiling on the rasterised size of a single page, in megapixels, evaluated against
    /// the page's declared geometry and the configured <see cref="Dpi"/> before anything is drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Chosen by arithmetic, and the arithmetic is worth stating because a bound nobody can check
    /// is a bound nobody can trust.</b> What 80 megapixels admits: A4 at 150 DPI is 2.2 MP; A4 at
    /// the 600 DPI ceiling is 34.8 MP; A3 at 600 DPI - the largest legitimate combination this
    /// configuration can express - is 69.5 MP. What it refuses: the 200 x 200 inch page measured
    /// above, 900 MP at the default DPI, by more than a factor of eleven.
    /// </para>
    /// <para>
    /// <b>What it costs in the worst admitted case:</b> 80 MP at 4 bytes per pixel is about 320 MB
    /// of transient bitmap, and only for a deployment that has deliberately set 600 DPI on A3-sized
    /// input. At the default DPI on the corpus this pipeline actually processes, the real figure is
    /// under 10 MB per page. PDFium serialises rendering behind its own lock, so this is a bound on
    /// one page at a time rather than on a concurrent multiple of it.
    /// </para>
    /// </remarks>
    public const int DefaultMaxPageMegapixels = 80;

    /// <summary>Upper bound on <see cref="MaxPageMegapixels"/> itself.</summary>
    /// <remarks>
    /// 1000 megapixels is 4 GB of bitmap. Nothing in this pipeline has a use for it; the cap exists
    /// so that raising the ceiling stays a bounded decision rather than an unbounded one.
    /// </remarks>
    public const int MaximumMaxPageMegapixels = 1000;

    /// <summary>
    /// The default ceiling on the rasterised size of a whole document, in megapixels, summed across
    /// its pages and evaluated before anything is drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a third ceiling: the first two multiply, and their product is unbounded.</b> A document
    /// can sit under the page-count ceiling AND under the per-page ceiling on every single page while
    /// still demanding an enormous amount of work in total - 500 pages of 69.5 MP each passes both
    /// and asks for 34,800 megapixels. Measured, on this machine: that is <b>23.8 minutes</b> of
    /// rendering, requested by a <b>102 KB</b> file. Neither existing ceiling sees it, because
    /// neither looks at the total.
    /// </para>
    /// <para>
    /// <b>Megapixels are the right unit because they are a proxy for time.</b> Measured across a
    /// thirtyfold range of page geometry, PDFium's throughput is nearly flat: A4 at 150 DPI renders
    /// 2.2 MP in 0.10 s and A3 at 600 DPI renders 69.6 MP in 2.86 s - 22 and 24 megapixels per
    /// second respectively. So a budget denominated in megapixels bounds wall-clock work to within
    /// about ten percent, without this type needing to know anything about the host's speed.
    /// </para>
    /// <para>
    /// <b>The arithmetic for 400.</b> What it admits: 184 A4 pages at the default DPI - a country
    /// manual runs to a handful (design 5.1), so this is two orders of magnitude of headroom - and,
    /// at the measured rate, about <b>18 seconds</b> of rendering. What it refuses: the 34,800 MP
    /// case above, by a factor of 87.
    /// </para>
    /// <para>
    /// <b>How this interacts with <see cref="MaxPageCount"/>, stated because at the defaults it
    /// mostly supersedes it.</b> At the default DPI on A4 this budget binds at 184 pages, well before
    /// the 500-page ceiling, so page count is rarely the check that fires. It is still worth keeping
    /// and still checked first: it is O(1) against a number the document declares, so it rejects the
    /// 5000-page case before any geometry is walked, and it catches documents whose pages are
    /// individually tiny - 5000 pages of one-point squares would pass this budget comfortably and is
    /// still not a document anyone should render.
    /// </para>
    /// </remarks>
    public const int DefaultMaxTotalMegapixels = 400;

    /// <summary>Upper bound on <see cref="MaxTotalMegapixels"/> itself.</summary>
    /// <remarks>
    /// At the measured ~23 megapixels per second, 20,000 megapixels is about fifteen minutes of
    /// rendering for one document. That is already past anything this pipeline should attempt in a
    /// single workflow step; the cap keeps raising the budget a bounded decision.
    /// </remarks>
    public const int MaximumMaxTotalMegapixels = 20_000;

    /// <summary>Initialises a new <see cref="PageImageRenderOptions"/>.</summary>
    /// <param name="dpi">
    /// Rasterisation density in dots per inch, between <see cref="MinimumDpi"/> and
    /// <see cref="MaximumDpi"/> inclusive. Defaults to <see cref="DefaultDpi"/>.
    /// </param>
    /// <param name="maxPageCount">
    /// Largest page count that will be rendered, between 1 and <see cref="MaximumMaxPageCount"/>.
    /// Defaults to <see cref="DefaultMaxPageCount"/>.
    /// </param>
    /// <param name="maxPageMegapixels">
    /// Largest rasterised page, in megapixels, between 1 and <see cref="MaximumMaxPageMegapixels"/>.
    /// Defaults to <see cref="DefaultMaxPageMegapixels"/>.
    /// </param>
    /// <param name="maxTotalMegapixels">
    /// Largest rasterised document, in megapixels summed across its pages, between 1 and
    /// <see cref="MaximumMaxTotalMegapixels"/>. Defaults to <see cref="DefaultMaxTotalMegapixels"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Any argument is outside its accepted range.</exception>
    public PageImageRenderOptions(
        int dpi = DefaultDpi,
        int maxPageCount = DefaultMaxPageCount,
        int maxPageMegapixels = DefaultMaxPageMegapixels,
        int maxTotalMegapixels = DefaultMaxTotalMegapixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, MinimumDpi);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dpi, MaximumDpi);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPageCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPageCount, MaximumMaxPageCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPageMegapixels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPageMegapixels, MaximumMaxPageMegapixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTotalMegapixels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxTotalMegapixels, MaximumMaxTotalMegapixels);

        // Deliberately NOT rejected: a total budget smaller than the per-page ceiling. It reads like
        // a contradiction and is not one - it simply means the aggregate binds first, which is a
        // coherent way to say "any geometry per page, but not much of it in one document". Refusing
        // the combination would invent a constraint neither bound implies.
        Dpi = dpi;
        MaxPageCount = maxPageCount;
        MaxPageMegapixels = maxPageMegapixels;
        MaxTotalMegapixels = maxTotalMegapixels;
    }

    /// <summary>Gets the rasterisation density in dots per inch.</summary>
    public int Dpi { get; }

    /// <summary>Gets the largest page count that will be rendered rather than refused.</summary>
    public int MaxPageCount { get; }

    /// <summary>Gets the largest rasterised page, in megapixels, that will be drawn rather than refused.</summary>
    public int MaxPageMegapixels { get; }

    /// <summary>Gets the largest rasterised document, in megapixels summed across its pages, that will be drawn rather than refused.</summary>
    public int MaxTotalMegapixels { get; }

    /// <summary>Gets <see cref="MaxTotalMegapixels"/> as a pixel count, the unit the pre-render check compares against.</summary>
    /// <remarks>
    /// <c>long</c> for the reason given on <see cref="MaxPagePixels"/>, and more urgently: this one
    /// accumulates across pages, so it exceeds <see cref="int"/> range at the default settings, let
    /// alone hostile ones.
    /// </remarks>
    public long MaxTotalPixels => (long)MaxTotalMegapixels * 1_000_000L;

    /// <summary>
    /// Gets <see cref="MaxPageMegapixels"/> as a pixel count, which is the unit the pre-render check
    /// actually compares against.
    /// </summary>
    /// <remarks>
    /// <c>long</c>, not <c>int</c>: the ceiling itself reaches 10^9 pixels, and the value being
    /// compared against it is an attacker-supplied geometry multiplied by a DPI ratio. Computing
    /// that product in 32 bits is how a bound gets defeated by overflow rather than by exceeding it.
    /// </remarks>
    public long MaxPagePixels => (long)MaxPageMegapixels * 1_000_000L;
}
