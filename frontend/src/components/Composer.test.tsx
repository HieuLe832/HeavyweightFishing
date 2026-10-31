import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { Composer } from "./Composer";

const box = () => screen.getByRole("textbox", { name: /message/i });

describe("Composer", () => {
  it("Enter sends the trimmed text and clears the box", async () => {
    const onSend = vi.fn();
    render(<Composer onSend={onSend} connected />);

    await userEvent.type(box(), "  hello there  {Enter}");

    expect(onSend).toHaveBeenCalledWith("hello there");
    expect(box()).toHaveValue("");
  });

  it("Shift+Enter inserts a newline instead of sending", async () => {
    const onSend = vi.fn();
    render(<Composer onSend={onSend} connected />);

    await userEvent.type(box(), "line one{Shift>}{Enter}{/Shift}line two");

    expect(onSend).not.toHaveBeenCalled();
    expect(box()).toHaveValue("line one\nline two");
  });

  it("does not send whitespace-only or newline-only text", async () => {
    const onSend = vi.fn();
    render(<Composer onSend={onSend} connected />);

    await userEvent.type(box(), "   {Enter}");
    await userEvent.type(box(), "{Shift>}{Enter}{Enter}{/Shift}{Enter}");
    await userEvent.click(screen.getByRole("button", { name: /send/i }));

    expect(onSend).not.toHaveBeenCalled();
  });

  it("does not send while an input method is composing (Vietnamese Telex, Chinese, Japanese)", () => {
    const onSend = vi.fn();
    render(<Composer onSend={onSend} connected />);

    // user-event does not model composition, so the event is built by hand.
    const textarea = box() as HTMLTextAreaElement;
    textarea.value = "xin chào";
    textarea.dispatchEvent(new Event("input", { bubbles: true }));
    textarea.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, isComposing: true }));

    expect(onSend).not.toHaveBeenCalled();
  });

  it("blocks text over 4000 characters and shows the counter", async () => {
    const onSend = vi.fn();
    render(<Composer onSend={onSend} connected />);

    await userEvent.click(box());
    await userEvent.paste("a".repeat(4001));
    await userEvent.keyboard("{Enter}");

    expect(onSend).not.toHaveBeenCalled();
    expect(screen.getByText(/4001\s*\/\s*4000/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /send/i })).toBeDisabled();
  });

  it("accepts exactly 4000 characters", async () => {
    const onSend = vi.fn();
    render(<Composer onSend={onSend} connected />);

    await userEvent.click(box());
    await userEvent.paste("b".repeat(4000));
    await userEvent.keyboard("{Enter}");

    expect(onSend).toHaveBeenCalledWith("b".repeat(4000));
  });

  it("is disabled with a reconnecting note when not connected", async () => {
    const onSend = vi.fn();
    render(<Composer onSend={onSend} connected={false} />);

    expect(box()).toBeDisabled();
    expect(screen.getByRole("button", { name: /send/i })).toBeDisabled();
    expect(screen.getByText(/reconnecting/i)).toBeInTheDocument();
  });
});
