import { Slot } from "@radix-ui/react-slot";
import { cva, type VariantProps } from "class-variance-authority";
import { Loader2 } from "lucide-react";
import type { ButtonHTMLAttributes } from "react";
import { cn } from "../cn";

const buttonVariants = cva(
  "inline-flex min-h-[var(--karry-control-height)] items-center justify-center gap-2 rounded-md px-4 font-semibold " +
    "border-[length:var(--karry-border-width)] transition-colors disabled:cursor-not-allowed disabled:opacity-60",
  {
    variants: {
      variant: {
        primary: "border-primary bg-primary text-primary-text hover:brightness-110",
        secondary: "border-border bg-surface text-text hover:bg-background",
        danger: "border-danger bg-danger text-danger-text hover:brightness-110",
        ghost: "border-transparent bg-transparent text-primary underline-offset-4 hover:underline",
      },
      block: { true: "w-full", false: "" },
    },
    defaultVariants: { variant: "primary", block: false },
  },
);

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement>, VariantProps<typeof buttonVariants> {
  /** Render the child element (a link) with button styling instead of a <button>. */
  asChild?: boolean;
  /** Shows a spinner, sets aria-busy and blocks clicks (no double submission). */
  loading?: boolean;
}

export function Button({ className, variant, block, asChild = false, loading = false, disabled, children, type, ...props }: ButtonProps) {
  const Component = asChild ? Slot : "button";
  return (
    <Component
      className={cn(buttonVariants({ variant, block }), className)}
      disabled={asChild ? undefined : disabled === true || loading}
      aria-busy={loading || undefined}
      type={asChild ? undefined : (type ?? "button")}
      {...props}
    >
      {loading && <Loader2 aria-hidden className="size-5 animate-spin" />}
      {children}
    </Component>
  );
}
