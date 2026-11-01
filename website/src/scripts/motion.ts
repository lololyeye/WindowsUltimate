// The site's motion layer: smooth wheel scrolling, headings that come in word by word, the statement
// that lights up as it is read, the hero window that straightens as the page scrolls, depth (parallax)
// and the cursor light on glass. Every part keeps the page usable without it. With reduced motion,
// nothing travels: the CSS keeps the fades, and the scroll-linked effects are not started.
import Lenis from "lenis";
import "lenis/dist/lenis.css";

const reduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
const finePointer = matchMedia("(hover: hover) and (pointer: fine)").matches;

// Smooth scrolling for the wheel and trackpad (touch keeps the native scroll). Kept with reduced
// motion: it only follows the wheel and never moves on its own.
const lenis = new Lenis({
  lerp: 0.085,
  autoRaf: true,
  anchors: { offset: -84 },
  allowNestedScroll: true,
});

// Headings are wrapped word by word; the CSS decides how the words come in.
// An element marked data-word (a gradient line) moves as one word, so its gradient stays whole.
const wrapWords = (el: HTMLElement, cls: "split" | "split-rise" | "scrub") => {
  let i = 0;
  const word = (node: Node) => {
    const w = document.createElement("span");
    w.className = cls === "scrub" ? "sw" : "w";
    w.style.setProperty("--i", String(i++));
    w.append(node);
    if (cls !== "split-rise") return w;
    const mask = document.createElement("span");
    mask.className = "wm";
    mask.append(w);
    return mask;
  };
  const walk = (node: Node) => {
    for (const child of [...node.childNodes]) {
      if (child.nodeType === Node.TEXT_NODE) {
        const frag = document.createDocumentFragment();
        for (const part of child.textContent!.split(/(\s+)/)) {
          if (!part) continue;
          frag.append(/^\s+$/.test(part) ? part : word(document.createTextNode(part)));
        }
        child.replaceWith(frag);
      } else if (child instanceof HTMLElement && child.hasAttribute("data-word")) {
        const placeholder = document.createComment("");
        child.replaceWith(placeholder);
        placeholder.replaceWith(word(child));
      } else if (child.nodeType === Node.ELEMENT_NODE && (child as Element).tagName !== "BR") {
        walk(child);
      }
    }
  };
  walk(el);
  el.classList.add(cls);
};
document.querySelectorAll<HTMLElement>("main h1, main .final h2").forEach((el) => wrapWords(el, "split"));
// The page title is on screen from the start: its words come in on the next frames, not on scroll.
document.querySelectorAll<HTMLElement>("main h1.split").forEach((el) => requestAnimationFrame(() => requestAnimationFrame(() => el.classList.add("in"))));
document.querySelectorAll<HTMLElement>("main .section-head h2").forEach((el) => wrapWords(el, "split-rise"));
const scrubs = [...document.querySelectorAll<HTMLElement>("[data-scrub]")];
scrubs.forEach((el) => wrapWords(el, "scrub"));

document.querySelectorAll<HTMLElement>("[data-stagger]").forEach((el) => {
  [...el.children].forEach((c, i) => (c as HTMLElement).style.setProperty("--i", String(i)));
});

if (finePointer) {
  document.querySelectorAll<HTMLElement>(".spot").forEach((el) => {
    el.addEventListener("pointermove", (e) => {
      const r = el.getBoundingClientRect();
      el.style.setProperty("--mx", `${e.clientX - r.left}px`);
      el.style.setProperty("--my", `${e.clientY - r.top}px`);
    });
  });
}

// Scroll-linked effects, computed once per frame while the page moves.
const stage = document.querySelector<HTMLElement>("[data-stage]");
const layers = [...document.querySelectorAll<HTMLElement>("[data-parallax]")];
const clamp = (v: number) => Math.min(1, Math.max(0, v));

const update = () => {
  const vh = innerHeight;
  if (stage) {
    // 0 when the hero window first shows, 1 once it has risen to the middle of the screen.
    const r = stage.getBoundingClientRect();
    stage.style.setProperty("--p", clamp(1 - (r.top - vh * 0.12) / (vh * 0.55)).toFixed(3));
  }
  for (const el of layers) {
    const r = el.getBoundingClientRect();
    if (r.bottom < -200 || r.top > vh + 200) continue;
    const offset = (r.top + r.height / 2 - vh / 2) * Number(el.dataset.parallax);
    el.style.transform = `translate3d(0, ${offset.toFixed(1)}px, 0)`;
  }
  for (const el of scrubs) {
    const r = el.getBoundingClientRect();
    const p = clamp((vh * 0.82 - r.top) / (r.height + vh * 0.32));
    const words = el.querySelectorAll<HTMLElement>(".sw");
    const lit = Math.round(p * words.length);
    words.forEach((w, i) => w.classList.toggle("on", i < lit));
  }
};

if (!reduced) {
  let queued = false;
  const queue = () => {
    if (queued) return;
    queued = true;
    requestAnimationFrame(() => {
      queued = false;
      update();
    });
  };
  lenis.on("scroll", queue);
  addEventListener("scroll", queue, { passive: true });
  addEventListener("resize", queue);
  update();
} else {
  stage?.style.setProperty("--p", "1");
  scrubs.forEach((el) => el.querySelectorAll(".sw").forEach((w) => w.classList.add("on")));
}

// Copy buttons (the SHA-256).
document.querySelectorAll<HTMLButtonElement>("[data-copy]").forEach((b) => {
  b.addEventListener("click", async () => {
    const target = document.querySelector<HTMLElement>(b.dataset.copy!);
    const label = b.querySelector<HTMLElement>(".copy-label") ?? b;
    const initial = label.textContent;
    try {
      await navigator.clipboard.writeText(target!.textContent!.trim());
      label.textContent = b.dataset.copied!;
      b.classList.add("done");
    } catch {
      label.textContent = b.dataset.fail!;
      if (target) getSelection()?.selectAllChildren(target);
    }
    setTimeout(() => {
      label.textContent = initial;
      b.classList.remove("done");
    }, 2200);
  });
});
