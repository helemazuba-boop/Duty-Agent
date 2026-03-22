import sys
import re

def refactor_core(file_path):
    with open(file_path, "r", encoding="utf-8") as f:
        content = f.read()

    # 1. Add http.server imports at the top
    if "from http.server import BaseHTTPRequestHandler, HTTPServer" not in content:
        content = re.sub(
            r"(import urllib\.request\n)",
            r"\1from http.server import BaseHTTPRequestHandler, HTTPServer\nimport threading\n",
            content
        )

    # 2. Extract run_duty_agent from main()
    # We find 'def main():'
    main_pattern = r"(def main\(\):\n\s+parser = argparse\.ArgumentParser\(.*?)\n\s+try:\n"
    
    # Actually, it's easier to find the whole main and replace it. Let's do a more robust string manipulation.
    # Find 'def main():\n'
    idx_main = content.find("def main():\n")
    if idx_main == -1:
        print("Could not find main()")
        return
        
    before_main = content[:idx_main]
    main_code = content[idx_main:]
    
    # We will provide a completely new main block and a DutyServer implementation.
    # We will extract the exact logic of the try-finally block of main()
    
    new_code = """
def run_duty_agent(ctx, input_data, emit_progress_fn=None):
    state_lock_path = ctx.paths["state"].with_suffix(ctx.paths["state"].suffix + ".lock")
    state_lock_acquired = False
    
    try:
        run_now = datetime.now()
        ctx.config = load_config(ctx)
        name_to_id, id_to_name, all_ids, id_to_active = load_roster(ctx.paths["roster"])
        acquire_state_file_lock(state_lock_path)
        state_lock_acquired = True
        state_data = load_state(ctx.paths["state"])

        input_data = merge_input_config(input_data)

        instruction = str(input_data.get("instruction", "")).strip()
        if not instruction:
            instruction = "按照要求排班"
        base_url = str(input_data.get("base_url", ctx.config.get("base_url", ""))).strip()
        model = str(input_data.get("model", ctx.config.get("model", ""))).strip()
        if not base_url or not model:
            raise ValueError("Missing config field: base_url/model.")
        ctx.config["base_url"] = base_url
        ctx.config["model"] = model
        ctx.config["api_key"] = load_api_key_from_env()
        ctx.config["llm_stream"] = parse_bool(
            input_data.get(
                "llm_stream",
                input_data.get("stream", ctx.config.get("llm_stream", ctx.config.get("stream", LLM_STREAM_ENABLED_DEFAULT))),
            ),
            LLM_STREAM_ENABLED_DEFAULT,
        )
        per_day = parse_int(
            input_data.get("per_day", ctx.config.get("per_day", DEFAULT_PER_DAY)),
            DEFAULT_PER_DAY,
            1,
            30,
        )
        area_names = normalize_area_names(input_data.get("area_names", ctx.config.get("area_names", [])))
        area_per_day_counts = normalize_area_per_day_counts(
            area_names,
            input_data.get("area_per_day_counts", ctx.config.get("area_per_day_counts", {})),
            per_day,
        )
        duty_rule = str(input_data.get("duty_rule", ctx.config.get("duty_rule", ""))).strip()
        apply_mode = str(input_data.get("apply_mode", "append")).strip().lower()
        prompt_mode = str(input_data.get("prompt_mode", "Regular")).strip()
        existing_notes = input_data.get("existing_notes", {})
        if not isinstance(existing_notes, dict):
            existing_notes = {}

        # Start date
        today_date = run_now.date()
        entries = get_pool_entries_with_date(state_data)
        if apply_mode == "append":
            if entries:
                start_date = entries[-1][1] + timedelta(days=1)
            else:
                start_date = today_date
        else:
            start_date = today_date

        sanitized_instruction = anonymize_instruction(instruction, name_to_id)
        duty_rule = anonymize_instruction(duty_rule, name_to_id)

        current_time = run_now.strftime("%Y-%m-%d %H:%M")
        
        # Load previous context (AI Memory)
        previous_context = str(state_data.get("next_run_note", "")).strip()
        debt_list = extract_ids_from_value(state_data.get("debt_list", []), set(all_ids), 9999)
        credit_list = extract_ids_from_value(state_data.get("credit_list", []), set(all_ids), 9999)

        messages = build_prompt_messages(
            all_ids=all_ids,
            id_to_active=id_to_active,
            current_time=current_time,
            instruction=sanitized_instruction,
            duty_rule=duty_rule,
            area_names=area_names,
            previous_context=previous_context,
            debt_list=debt_list,
            credit_list=credit_list,
            prompt_mode=prompt_mode,
        )

        llm_result, llm_response_text = call_llm(
            messages,
            ctx.config,
            progress_callback=emit_progress_fn if emit_progress_fn else emit_progress_line,
        )
        schedule_raw = llm_result.get("schedule", [])
        validate_llm_schedule_entries(schedule_raw)
        
        # Persist next_run_note for future runs
        next_run_note = str(llm_result.get("next_run_note", "")).strip()
        state_data["next_run_note"] = next_run_note

        normalized_ids = normalize_multi_area_schedule_ids(
            schedule_raw,
            all_ids,
            area_names,
            area_per_day_counts,
        )

        # --- Debt Recovery Audit ---
        raw_new_debts = llm_result.get("new_debt_ids", [])
        new_debt_list = extract_ids_from_value(raw_new_debts, set(all_ids), 9999)
        state_data["debt_list"] = recover_missing_debts(
            original_debt_list=debt_list,
            new_debt_ids_from_llm=new_debt_list,
            normalized_schedule=normalized_ids,
        )

        # --- Credit List Persistence ---
        raw_new_credits = llm_result.get("new_credit_ids", [])
        new_credit_list = extract_ids_from_value(raw_new_credits, set(all_ids), 9999)
        state_data["credit_list"] = reconcile_credit_list(
            original_credit_list=credit_list,
            new_credit_ids_from_llm=new_credit_list,
            normalized_schedule=normalized_ids,
            valid_ids=set(all_ids),
            debt_list=state_data.get("debt_list", []),
            has_llm_field="new_credit_ids" in llm_result,
        )

        restored = restore_schedule(
            normalized_ids,
            id_to_name,
            area_names,
            existing_notes,
        )
        if not restored:
            raise ValueError("LLM returned no valid schedule entries.")
        state_data["schedule_pool"] = merge_schedule_pool(state_data, restored, apply_mode, start_date)

        save_json_atomic(ctx.paths["state"], state_data)
        
        return {
            "status": "success",
            "message": "",
            "ai_response": (llm_response_text or "")[:AI_RESPONSE_MAX_CHARS]
        }
    except Exception as ex:
        traceback.print_exc()
        return {
            "status": "error",
            "message": str(ex)
        }
    finally:
        if state_lock_acquired:
            try:
                release_state_file_lock(state_lock_path)
            except Exception:
                pass

class ServerHandler(BaseHTTPRequestHandler):
    data_dir = None
    
    def log_message(self, format, *args):
        pass

    def do_POST(self):
        if self.path == '/schedule':
            content_length = int(self.headers.get('Content-Length', 0))
            if content_length > 0:
                body = self.rfile.read(content_length).decode('utf-8')
                try:
                    input_data = json.loads(body)
                except:
                    input_data = {}
            else:
                input_data = {}
            
            ctx = Context(self.data_dir)
            
            self.send_response(200)
            self.send_header('Content-Type', 'text/event-stream; charset=utf-8')
            self.send_header('Cache-Control', 'no-cache')
            self.send_header('Connection', 'close')
            self.end_headers()
            
            def sse_progress(phase, message, stream_chunk=None):
                event_data = {
                    "phase": phase,
                    "message": message
                }
                if stream_chunk:
                    event_data["stream_chunk"] = stream_chunk
                
                payload = json.dumps(event_data, ensure_ascii=False)
                try:
                    self.wfile.write(f"data: {payload}\\n\\n".encode('utf-8'))
                    self.wfile.flush()
                except Exception:
                    pass
            
            result = run_duty_agent(ctx, input_data, sse_progress)
            
            try:
                final_payload = json.dumps(result, ensure_ascii=False)
                self.wfile.write(f"event: complete\\ndata: {final_payload}\\n\\n".encode('utf-8'))
                self.wfile.flush()
            except Exception:
                pass
                
        elif self.path == '/shutdown':
            self.send_response(200)
            self.send_header('Content-Type', 'application/json; charset=utf-8')
            self.end_headers()
            self.wfile.write(b'{"status":"shutting down"}')
            self.wfile.flush()
            threading.Thread(target=self.server.shutdown, daemon=True).start()
        else:
            self.send_response(404)
            self.end_headers()

def main():
    parser = argparse.ArgumentParser(description="Duty-Agent Core")
    parser.add_argument("--data-dir", type=str, default="data")
    parser.add_argument("--server", action="store_true", help="Run in HTTP server mode")
    parser.add_argument("--port", type=int, default=0, help="Port to listen on (0 for random)")
    args = parser.parse_args()

    data_dir = Path(args.data_dir).resolve()
    data_dir.mkdir(parents=True, exist_ok=True)
    
    if args.server:
        ServerHandler.data_dir = data_dir
        httpd = HTTPServer(('127.0.0.1', args.port), ServerHandler)
        print(f"__DUTY_SERVER_PORT__:{httpd.server_port}", flush=True)
        httpd.serve_forever()
    else:
        ctx = Context(data_dir)
        input_data = {}
        if ctx.paths["input"].exists():
            with open(ctx.paths["input"], "r", encoding="utf-8-sig") as f:
                input_data = json.load(f)
                
        result = run_duty_agent(ctx, input_data)
        
        extra = {}
        if "ai_response" in result:
            extra["ai_response"] = result["ai_response"]
        
        write_result(
            ctx.paths["result"],
            result["status"],
            result["message"],
            extra=extra
        )

if __name__ == "__main__":
    main()
"""

    with open(file_path, "w", encoding="utf-8") as f:
        f.write(before_main + new_code)
        
    print("core.py successfully refactored.")

if __name__ == "__main__":
    refactor_core(r"c:\\Users\\ZhuanZ\\OneDrive\\Desktop\\Duty-Agent\\Assets_Duty\\core.py")
