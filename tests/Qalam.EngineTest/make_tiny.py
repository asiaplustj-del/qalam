import numpy as np, gguf, sys
out = sys.argv[1]
n_embd, n_head, n_layer, n_ff = 64, 4, 2, 128
tokens = ["<unk>", "<s>", "</s>"]
types = [gguf.TokenType.UNKNOWN, gguf.TokenType.CONTROL, gguf.TokenType.CONTROL]
for b in range(256):
    tokens.append(f"<0x{b:02X}>"); types.append(gguf.TokenType.BYTE)
for w in ["▁", "▁a", "▁the", "▁и", "▁в", "а", "о", "е", "н", "т"] + [chr(c) for c in range(ord('a'), ord('z')+1)]:
    tokens.append(w); types.append(gguf.TokenType.NORMAL)
n_vocab = len(tokens)
w = gguf.GGUFWriter(out, "llama")
w.add_name("qalam-tiny-test")
w.add_context_length(2048); w.add_embedding_length(n_embd); w.add_block_count(n_layer)
w.add_feed_forward_length(n_ff); w.add_head_count(n_head); w.add_head_count_kv(n_head)
w.add_rope_dimension_count(n_embd // n_head); w.add_layer_norm_rms_eps(1e-5)
w.add_vocab_size(n_vocab); w.add_file_type(gguf.LlamaFileType.ALL_F32)
w.add_tokenizer_model("llama"); w.add_token_list(tokens)
w.add_token_scores([0.0] * n_vocab); w.add_token_types(types)
w.add_bos_token_id(1); w.add_eos_token_id(2); w.add_unk_token_id(0)
rng = np.random.default_rng(0)
r = lambda *s: (rng.standard_normal(s) * 0.02).astype(np.float32)
w.add_tensor("token_embd.weight", r(n_vocab, n_embd))
w.add_tensor("output_norm.weight", np.ones(n_embd, np.float32))
w.add_tensor("output.weight", r(n_vocab, n_embd))
for i in range(n_layer):
    w.add_tensor(f"blk.{i}.attn_norm.weight", np.ones(n_embd, np.float32))
    for t in ("attn_q", "attn_k", "attn_v", "attn_output"):
        w.add_tensor(f"blk.{i}.{t}.weight", r(n_embd, n_embd))
    w.add_tensor(f"blk.{i}.ffn_norm.weight", np.ones(n_embd, np.float32))
    w.add_tensor(f"blk.{i}.ffn_gate.weight", r(n_ff, n_embd))
    w.add_tensor(f"blk.{i}.ffn_up.weight", r(n_ff, n_embd))
    w.add_tensor(f"blk.{i}.ffn_down.weight", r(n_embd, n_ff))
w.write_header_to_file(); w.write_kv_data_to_file(); w.write_tensors_to_file(); w.close()
print("ok", n_vocab)
