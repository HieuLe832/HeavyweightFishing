import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { Bobber } from "./Bobber";
import { Button } from "./Button";
import { Field } from "./Field";

describe("Button", () => {
  it("renders its label and fires onClick", async () => {
    const onClick = vi.fn();
    render(<Button onClick={onClick}>Cast off</Button>);

    await userEvent.click(screen.getByRole("button", { name: "Cast off" }));

    expect(onClick).toHaveBeenCalledOnce();
  });

  it("is disabled and not clickable while busy", async () => {
    const onClick = vi.fn();
    render(<Button busy onClick={onClick}>Cast off</Button>);

    const button = screen.getByRole("button");
    await userEvent.click(button);

    expect(button).toBeDisabled();
    expect(button).toHaveAttribute("aria-busy", "true");
    expect(onClick).not.toHaveBeenCalled();
  });
});

describe("Bobber", () => {
  it("renders nothing at 0", () => {
    const { container } = render(<Bobber count={0} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("shows the count, capped at 99+", () => {
    const { rerender } = render(<Bobber count={7} />);
    expect(screen.getByText("7")).toBeInTheDocument();

    rerender(<Bobber count={100} />);
    expect(screen.getByText("99+")).toBeInTheDocument();
  });

  it("has an accessible name", () => {
    render(<Bobber count={3} />);
    expect(screen.getByLabelText("3 unread")).toBeInTheDocument();
  });
});

describe("Field", () => {
  it("labels the input and shows the error", () => {
    render(<Field label="Email" error="Enter a valid email address." />);

    const input = screen.getByLabelText("Email");
    expect(input).toHaveAccessibleDescription("Enter a valid email address.");
    expect(input).toBeInvalid();
  });
});
