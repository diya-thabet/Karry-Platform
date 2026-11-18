import { Component, type ReactNode } from "react";

/**
 * Catches a part of a form that could not load (a lazy chunk missing offline) and shows `fallback` instead of an empty
 * page: the operator keeps the form, and its draft, and can try again once connected.
 */
export class PartBoundary extends Component<{ fallback: ReactNode; children: ReactNode }, { failed: boolean }> {
  override state = { failed: false };

  static getDerivedStateFromError(): { failed: boolean } {
    return { failed: true };
  }

  override render(): ReactNode {
    return this.state.failed ? this.props.fallback : this.props.children;
  }
}
