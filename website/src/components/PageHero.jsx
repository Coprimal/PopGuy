/** The shared top of the inner pages (Actions, Docs, Privacy): kicker, title, lead, and the mascot peeking in. */
export function PageHero({ kicker, title, lead, children }) {
  return (
    <section className="page-hero">
      <div className="page-hero__bg" aria-hidden="true" />
      <div className="container page-hero__inner">
        <div>
          <p className="kicker">{kicker}</p>
          <h1>{title}</h1>
          {lead ? <p className="lead">{lead}</p> : null}
          {children}
        </div>
        <img className="page-hero__mascot" src="/mascot.png" alt="" width="104" height="104" />
      </div>
    </section>
  );
}
