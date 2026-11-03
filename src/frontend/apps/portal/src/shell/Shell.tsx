import { useRouteContext } from "@tanstack/react-router";
import { Layout } from "./Layout";
import { NotFound } from "./Placeholders";
import { OperatorLayout, OperatorNotFound } from "../operator/OperatorConsole";

/** The company's portal shell, or the operator console's for Karry staff; unknown addresses show inside it. */
export function Shell() {
  const { operator } = useRouteContext({ from: "__root__" });
  return operator ? <OperatorLayout /> : <Layout />;
}

export function ShellNotFound() {
  const { operator } = useRouteContext({ from: "__root__" });
  return operator ? <OperatorNotFound /> : <NotFound />;
}
