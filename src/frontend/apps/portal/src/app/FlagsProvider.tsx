import { OpenFeature, OpenFeatureProvider } from "@openfeature/react-sdk";
import { useEffect, type ReactNode } from "react";
import { useApi } from "./api";
import { FlagDomain, KarryFlagProvider } from "./flags";

/** Sets Karry's flag provider for this app instance and gives its components the portal's flag domain. */
export function FlagsProvider({ children }: { children: ReactNode }) {
  const api = useApi();
  useEffect(() => {
    void OpenFeature.setProviderAndWait(FlagDomain, new KarryFlagProvider(api)).catch(() => undefined); // defaults apply
  }, [api]);
  return (
    <OpenFeatureProvider domain={FlagDomain} suspend={false}>
      {children}
    </OpenFeatureProvider>
  );
}
