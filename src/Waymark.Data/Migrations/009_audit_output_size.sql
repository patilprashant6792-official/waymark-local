-- The number of tokens, counted with OpenAI's o200k_base tokenizer, that a tool call's output added to the AI's context.
-- Null on rows recorded before tokens were counted; the totals estimate those from result_bytes.
ALTER TABLE audit_entries ADD COLUMN result_tokens INTEGER;
