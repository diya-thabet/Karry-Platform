import "@testing-library/jest-dom/vitest";
import { configure } from "@testing-library/react";

// As in the portal: a busy CI runner may take more than the default 1 s of findBy*/waitFor to load a lazy part.
configure({ asyncUtilTimeout: 5000 });
